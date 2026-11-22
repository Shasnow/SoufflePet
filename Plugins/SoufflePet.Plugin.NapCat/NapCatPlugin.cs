// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SoufflePet.Plugin.Sdk;

namespace SoufflePet.Plugins.NapCat;

/// <summary>
/// NapCat（OneBot 11）消息平台：以正向 WebSocket 客户端连接 NapCat，接收消息事件并用同一连接发送 action。
/// 群聊里 @ 到桌宠、群聊里命中唤醒词（无需 @）、以及允许用户的私聊会通过
/// <see cref="OnMentioned" /> 主动唤醒桌宠；还可为群聊配置概率回复，让非 @、非引用、未命中唤醒词的消息也按概率随机唤醒。
/// 插件始终忽略桌宠自己发出的消息，避免自己回复自己。
/// 支持按会话 id（group:群号 / private:QQ号）读写指定会话的消息。
/// 同时提供群管理工具（禁言、解除禁言、全员禁言、踢出成员、修改群名片、查询成员），并按配置自动审批加群申请。
/// 收到戳一戳时可以转发给桌宠智能回复，或按概率自动回戳，并对同一用户做冷却。
/// 回复里可以用 [quote:被引用的原句] 引用消息、用 [at:QQ号或昵称] @ 别人，插件会把它们替换成真实消息段。
/// </summary>
public sealed class NapCatPlugin : PluginBase, IMessagePlatform
{
    private NapCatConfig _config = new();
    private NapCatConnection? _connection;
    private NapCatConversationStore? _store;
    private NapCatSender? _sender;
    private NapCatAttachmentDownloader? _downloader;
    private NapCatForwardResolver? _forwardResolver;
    private NapCatQuoteTracker? _quotes;
    private NapCatGroupAdmin? _groupAdmin;
    private NapCatGroupJoinApprover? _joinApprover;
    private NapCatPokeResponder? _pokeResponder;

    /// <summary>机器人自己的 QQ 号，来自事件里的 self_id；用于识别 @ 与过滤自己的消息。</summary>
    private long _selfId;

    /// <summary>历史补拉是否正在执行，避免重连抖动时并发拉取。</summary>
    private int _historyBackfillRunning;

    /// <summary>告诉模型如何主动引用消息与 @ 别人；插件会把标记替换成真实消息段。</summary>
    private const string InteractionHint =
        "需要引用某条消息时，在回复开头写 [quote:被引用的原句片段]；需要 @ 某人时写 [at:QQ号或昵称]，"
        + "[at:all] 表示 @全体成员。插件会把这些标记替换成真实引用和 @；不需要时就不要写。";

    /// <summary>平台 id，供工具的 platform 参数使用。</summary>
    public string Id => "napcat";

    /// <summary>平台提示词：角色说明（用户配置）加上引用与 @ 的写法，交给宿主注入系统提示词。</summary>
    public string? PlatformPrompt => _config.RolePrompt.Length == 0
        ? InteractionHint
        : _config.RolePrompt + "\n" + InteractionHint;

    /// <summary>收到需要桌宠处理的消息时触发，由宿主订阅后唤醒桌宠。</summary>
    public event EventHandler<PlatformIncomingMessage>? OnMentioned;

    /// <summary>只有插件设置里「命令管理员」列出的 QQ 号才能在平台消息里执行 /model 等命令。</summary>
    public bool AllowsPlatformCommands(string? senderId) =>
        long.TryParse(senderId?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId)
        && _config.CommandAdmins.Contains(userId);

    protected override Task OnInitializedAsync(CancellationToken cancellationToken)
    {
        _config = NapCatConfig.Load(Context.Configuration, Context.PluginDirectory);

        _store = new NapCatConversationStore(Context.Logger, Context.DataDirectory);
        _store.Load();
        _store.StartAutoSave();

        _connection = new NapCatConnection(Context.Logger, _config.AccessToken, HandleEvent);
        // 每次连上（含重连）都补拉一次历史，把桌宠离线期间的消息补进本地会话缓存
        _connection.Connected += () => _ = BackfillHistoryAsync();
        _quotes = new NapCatQuoteTracker(Context.Logger, _connection);
        _sender = new NapCatSender(Context.Logger, _connection, _store, _config, () => Volatile.Read(ref _selfId));
        _groupAdmin = new NapCatGroupAdmin(Context.Logger, _connection, _config);
        _joinApprover = new NapCatGroupJoinApprover(Context.Logger, _connection, _config);
        _pokeResponder = new NapCatPokeResponder(Context.Logger, _connection, _config, HandlePokeResponse);
        _downloader = new NapCatAttachmentDownloader(
            Context.Logger,
            _config.AttachmentCacheDir,
            _connection.ShutdownToken);
        _forwardResolver = new NapCatForwardResolver(
            Context.Logger,
            _connection,
            () => Volatile.Read(ref _selfId));

        if (!Uri.TryCreate(_config.WsUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != "ws" && uri.Scheme != "wss"))
        {
            Context.Logger.LogWarning("NapCat WebSocket 地址无效：{Url}，插件不会发起连接", _config.WsUrl);
            return Task.CompletedTask;
        }

        Context.Logger.LogInformation(
            "NapCat 平台将连接 {Url}；私聊白名单 {Users} 个，群白名单 {Groups} 个，可管理群 {ManagedGroups} 个，自动审批加群 {AutoApprove}",
            _config.WsUrl,
            _config.AllowedUsers.Count,
            _config.AllowedGroups.Count,
            _config.ManagedGroups.Count,
            _config.AutoApproveJoin ? "已开启" : "已关闭");

        _connection.Start(uri);
        return Task.CompletedTask;
    }

    protected override void OnToolsBuilding(ICollection<AIFunction> tools)
    {
        if (_groupAdmin is null || !_config.HasManagedGroups) return;

        tools.Add(AIFunctionFactory.Create(
            BanMemberAsync,
            "qqgroup_ban_member",
            "在指定 QQ 群里禁言某个成员；durationSeconds 填 0 或负数表示解除禁言。"));

        tools.Add(AIFunctionFactory.Create(
            SetWholeBanAsync,
            "qqgroup_ban_all",
            "开启或关闭指定 QQ 群的全员禁言。"));

        tools.Add(AIFunctionFactory.Create(
            KickMemberAsync,
            "qqgroup_kick_member",
            "把指定 QQ 群的某个成员移出群。"));

        tools.Add(AIFunctionFactory.Create(
            SetCardAsync,
            "qqgroup_set_card",
            "修改指定 QQ 群中某个成员的群名片。"));

        tools.Add(AIFunctionFactory.Create(
            MemberListAsync,
            "qqgroup_member_list",
            "读取指定 QQ 群的成员列表，用于确认要操作的对象。"));
    }

    private Task<string> BanMemberAsync(
        [Description("群号")] long groupId,
        [Description("要禁言的成员 QQ 号")]
        long userId,
        [Description("禁言时长（秒），最长 2592000（30 天）；填 0 或负数表示解除禁言")]
        int durationSeconds,
        CancellationToken cancellationToken = default)
        => _groupAdmin!.BanMemberAsync(groupId, userId, durationSeconds, cancellationToken);

    private Task<string> SetWholeBanAsync(
        [Description("群号")] long groupId,
        [Description("true 开启全员禁言，false 关闭")]
        bool enable,
        CancellationToken cancellationToken = default)
        => _groupAdmin!.SetWholeBanAsync(groupId, enable, cancellationToken);

    private Task<string> KickMemberAsync(
        [Description("群号")] long groupId,
        [Description("要移出群的成员 QQ 号")]
        long userId,
        [Description("是否同时拒绝该成员再次加群，默认 false")]
        bool rejectAddRequest = false,
        CancellationToken cancellationToken = default)
        => _groupAdmin!.KickMemberAsync(groupId, userId, rejectAddRequest, cancellationToken);

    private Task<string> SetCardAsync(
        [Description("群号")] long groupId,
        [Description("要修改群名片的成员 QQ 号")]
        long userId,
        [Description("新的群名片；留空表示清空")]
        string card,
        CancellationToken cancellationToken = default)
        => _groupAdmin!.SetCardAsync(groupId, userId, card, cancellationToken);

    private Task<string> MemberListAsync(
        [Description("群号")] long groupId,
        [Description("本次最多返回多少名成员，默认 50，上限 2000")]
        int count = 50,
        [Description("从第几名成员开始返回（从 0 开始），默认 0；用于翻页")]
        int offset = 0,
        CancellationToken cancellationToken = default)
        => _groupAdmin!.GetMemberListAsync(groupId, count, offset, cancellationToken);

    /// <summary>向指定会话发送消息；conversationId 形如 group:12345 或 private:777。</summary>
    public async Task SendMessageAsync(string conversationId, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        if (!NapCatConversationId.TryParse(conversationId, out var conversation))
            throw new ArgumentException(
                $"无法识别的会话 id「{conversationId}」，应形如 group:12345 或 private:777。",
                nameof(conversationId));

        await _sender!.SendMessageAsync(conversation, message).ConfigureAwait(false);
    }

    /// <summary>向指定会话发送图片或文件；conversationId 形如 group:12345 或 private:777。</summary>
    public async Task SendAttachmentAsync(string conversationId, PlatformAttachment attachment)
    {
        if (!NapCatConversationId.TryParse(conversationId, out var conversation))
            throw new ArgumentException(
                $"无法识别的会话 id「{conversationId}」，应形如 group:12345 或 private:777。",
                nameof(conversationId));

        await _sender!.SendAttachmentAsync(conversation, attachment).ConfigureAwait(false);
    }

    /// <summary>列出本次运行期间收到过消息的会话，按最近活跃排序。</summary>
    public Task<IReadOnlyList<PlatformConversation>> GetConversationsAsync()
        => Task.FromResult(_store!.GetConversations());

    /// <summary>
    /// 读取指定会话最近的消息：优先向 NapCat 查询历史（含插件启动前的消息），
    /// 未连接或查询失败时退回本次运行缓存。
    /// </summary>
    public async Task<string> GetMessagesAsync(string conversationId, int limit = 20)
    {
        var id = NapCatConversationId.Normalize(conversationId);
        if (id.Length == 0 || !NapCatConversationId.TryParse(id, out var conversation)) return string.Empty;

        var count = Math.Clamp(limit, 1, NapCatConversationStore.MaxHistoryMessages);
        var remote = await FetchHistoryAsync(conversation, count).ConfigureAwait(false);
        if (remote.Count > 0) return string.Join('\n', remote);

        var history = _store!.GetRecentLines(id, count);
        return history.Count == 0 ? string.Empty : string.Join('\n', history);
    }

    /// <summary>
    /// 通过 OneBot 历史接口拉取会话最近的消息；未连接、接口不支持或返回为空时返回空列表，
    /// 由调用方退回本地缓存。
    /// </summary>
    private async Task<IReadOnlyList<string>> FetchHistoryAsync(
        (string Kind, long Id) conversation,
        int count)
    {
        var socket = _connection!.Socket;
        if (socket is not { State: WebSocketState.Open }) return [];

        var id = $"{conversation.Kind}:{conversation.Id.ToString(CultureInfo.InvariantCulture)}";
        var isGroup = conversation.Kind == "group";
        var parameters = isGroup
            ? new Dictionary<string, object?>
            {
                ["group_id"] = conversation.Id,
                ["count"] = count,
            }
            : new Dictionary<string, object?>
            {
                ["user_id"] = conversation.Id,
                ["count"] = count,
            };

        try
        {
            var response = await _connection.SendActionAsync(
                    socket,
                    isGroup ? "get_group_msg_history" : "get_friend_msg_history",
                    parameters)
                .ConfigureAwait(false);

            if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return [];
            if (!data.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                return [];

            var selfId = Volatile.Read(ref _selfId);
            var lines = new List<string>();
            // 合并转发要逐条调接口，一次读历史最多反查 5 条，其余退回「[合并转发]」
            var forwardBudget = 5;
            foreach (var message in messages.EnumerateArray())
            {
                if (message.ValueKind != JsonValueKind.Object) continue;

                var parsed = NapCatMessageParser.ParseMessage(
                    message,
                    selfId,
                    collectForwards: true);
                var text = parsed.Text;
                // 只有 @ / 引用、没有正文的消息也是历史的一部分（展示行会写「无正文」）
                if (string.IsNullOrWhiteSpace(text) && !parsed.MentionsSelf && parsed.QuotedId is null) continue;

                if (parsed.Forwards is { Count: > 0 } historyForwards)
                {
                    if (forwardBudget > 0)
                    {
                        var batch = historyForwards.Take(forwardBudget).ToArray();
                        forwardBudget -= batch.Length;
                        text = await _forwardResolver!.HydrateAsync(text, batch).ConfigureAwait(false);
                    }

                    text = NapCatForwardResolver.Fallback(text, historyForwards);
                }

                var sender = NapCatMessageParser.GetSenderName(
                    message,
                    NapCatMessageParser.GetInt64(message, "user_id"));
                // @ 与引用同样是字段：引用摘要优先取反查缓存，没缓存过就只显示「引用」
                var quote = parsed.QuotedId is { } quotedHistoryId ? _quotes!.TryGetQuote(quotedHistoryId) : null;
                lines.Add(NapCatStoredMessage.Format(
                    sender, text, parsed.MentionsSelf, quote, parsed.QuotedId is not null));
            }

            Context.Logger.LogInformation("从 NapCat 读取会话 {Conversation} 的历史消息 {Count} 条", id, lines.Count);
            return lines;
        }
        catch (Exception ex)
        {
            Context.Logger.LogDebug(ex, "读取 NapCat 历史消息失败，改用本地缓存");
            return [];
        }
    }

    public override async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null) await _connection.ShutdownAsync().ConfigureAwait(false);
        _store?.StopAutoSave();
    }

    /// <summary>
    /// 连上 NapCat 后补拉已知会话的历史，把桌宠离线（或断线）期间的消息合并进本地缓存，
    /// 这样唤醒上下文、[quote:...] 与 [at:...] 都能看到这段时间的消息。
    /// </summary>
    private async Task BackfillHistoryAsync()
    {
        if (Interlocked.CompareExchange(ref _historyBackfillRunning, 1, 0) != 0) return;

        try
        {
            var socket = _connection?.Socket;
            if (socket is not { State: WebSocketState.Open }) return;

            await EnsureSelfIdAsync(socket).ConfigureAwait(false);

            var conversations = _store!.GetConversations();
            if (conversations.Count == 0) return;

            var total = 0;
            foreach (var conversation in conversations)
            {
                if (!NapCatConversationId.TryParse(conversation.ConversationId, out var target)) continue;
                // 白名单外的会话不补拉：这些消息本来也不会唤醒桌宠
                if (target.Kind == "group"
                    && _config.AllowedGroups.Count > 0
                    && !_config.AllowedGroups.Contains(target.Id)) continue;
                if (target.Kind == "private"
                    && _config.AllowedUsers.Count > 0
                    && !_config.AllowedUsers.Contains(target.Id)) continue;

                var messages = await FetchRemoteMessagesAsync(socket, target).ConfigureAwait(false);
                if (messages.Count == 0) continue;

                _store.MergeHistory(conversation.ConversationId, messages);
                total += messages.Count;
            }

            Context.Logger.LogInformation(
                "已从 NapCat 补拉 {Conversations} 个会话的历史，共 {Count} 条",
                conversations.Count,
                total);
        }
        catch (OperationCanceledException)
        {
            // 插件正在卸载，忽略
        }
        catch (Exception ex)
        {
            Context.Logger.LogDebug(ex, "补拉 NapCat 历史消息失败");
        }
        finally
        {
            Interlocked.Exchange(ref _historyBackfillRunning, 0);
        }
    }

    /// <summary>首次连上时先取一次自身 QQ 号，补拉的历史才能正确区分「自己发的」与「@ 了你」。</summary>
    private async Task EnsureSelfIdAsync(ClientWebSocket socket)
    {
        if (Volatile.Read(ref _selfId) > 0) return;

        try
        {
            var response = await _connection!
                .SendActionAsync(socket, "get_login_info", new Dictionary<string, object?>())
                .ConfigureAwait(false);
            if (response.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                var id = NapCatMessageParser.GetInt64(data, "user_id");
                if (id > 0) Volatile.Write(ref _selfId, id);
            }
        }
        catch (OperationCanceledException)
        {
            // 正在卸载，忽略
        }
        catch (Exception ex)
        {
            Context.Logger.LogDebug(ex, "补拉历史前获取自身 QQ 号失败");
        }
    }

    /// <summary>
    /// 拉取会话最近的远端历史，转成可直接合并进本地缓存的记录。
    /// 不下载附件、不逐条反查合并转发与引用，避免启动时堆积大量 action。
    /// </summary>
    private async Task<IReadOnlyList<NapCatStoredMessage>> FetchRemoteMessagesAsync(
        ClientWebSocket socket,
        (string Kind, long Id) conversation)
    {
        var isGroup = conversation.Kind == "group";
        var parameters = isGroup
            ? new Dictionary<string, object?>
            {
                ["group_id"] = conversation.Id,
                ["count"] = NapCatConversationStore.MaxHistoryMessages,
            }
            : new Dictionary<string, object?>
            {
                ["user_id"] = conversation.Id,
                ["count"] = NapCatConversationStore.MaxHistoryMessages,
            };

        var response = await _connection!
            .SendActionAsync(
                socket,
                isGroup ? "get_group_msg_history" : "get_friend_msg_history",
                parameters)
            .ConfigureAwait(false);

        if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return [];
        if (!data.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            return [];

        var selfId = Volatile.Read(ref _selfId);
        var results = new List<NapCatStoredMessage>(messages.GetArrayLength());
        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object) continue;

            var messageId = NapCatMessageParser.GetString(message, "message_id");
            if (string.IsNullOrWhiteSpace(messageId)) continue;

            var parsed = NapCatMessageParser.ParseMessage(message, selfId, collectForwards: true);
            var text = parsed.Text;
            // 只有 @ / 引用、没有正文的消息也保留，展示行会写「无正文」
            if (string.IsNullOrWhiteSpace(text) && !parsed.MentionsSelf && parsed.QuotedId is null) continue;

            if (parsed.Forwards is { Count: > 0 } forwards)
                text = NapCatForwardResolver.Fallback(text, forwards);

            var userId = NapCatMessageParser.GetInt64(message, "user_id");
            var seconds = NapCatMessageParser.GetInt64(message, "time");
            results.Add(new NapCatStoredMessage(
                messageId.Trim(),
                text,
                userId,
                NapCatMessageParser.GetSenderName(message, userId),
                seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : DateTimeOffset.UtcNow,
                parsed.MentionsSelf,
                parsed.QuotedId));
        }

        return results;
    }

    /// <summary>NapCat 事件分派：消息交给平台逻辑，加群申请交给自动审批，戳一戳交给响应器。</summary>
    private void HandleEvent(JsonElement root)
    {
        switch (NapCatMessageParser.GetString(root, "post_type"))
        {
            case "message":
                Context.Logger.LogDebug("收到 NapCat 消息：{Event}", root.ToString());
                HandleMessage(root);
                break;

            case "notice":
                Context.Logger.LogDebug("收到 NapCat 通知：{Event}", root.ToString());
                // 戳一戳等通知同样带 self_id：不写回的话，重启后首个事件是戳一戳时
                // _selfId 仍为 0，回复会被记成 UserId=0，下次就找不到「上次回复」。
                var noticeSelfId = NapCatMessageParser.GetInt64(root, "self_id");
                if (noticeSelfId > 0) Volatile.Write(ref _selfId, noticeSelfId);
                _pokeResponder?.Handle(root, Volatile.Read(ref _selfId));
                break;

            case "request":
                // 审批要等待 action 响应，不能在接收循环里同步处理
                var request = root.Clone();
                _ = Task.Run(async () =>
                    {
                        try
                        {
                            await HandleJoinRequestAsync(request, _connection!.ShutdownToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // 插件正在卸载，忽略
                        }
                        catch (Exception ex)
                        {
                            Context.Logger.LogWarning(ex, "自动审批加群申请失败");
                        }
                    }, CancellationToken.None).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleJoinRequestAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (_joinApprover is null || !_joinApprover.ShouldHandle(root)) return;
        await _joinApprover.HandleAsync(root, cancellationToken).ConfigureAwait(false);
    }

    private void HandleMessage(JsonElement root)
    {
        var incomingSelfId = NapCatMessageParser.GetInt64(root, "self_id");
        if (incomingSelfId > 0) Volatile.Write(ref _selfId, incomingSelfId);
        var selfId = Volatile.Read(ref _selfId);

        var userId = NapCatMessageParser.GetInt64(root, "user_id");
        if (userId <= 0) return;
        // 始终忽略桌宠自己发出的消息，避免自己回复自己
        if (userId == selfId) return;

        var parsed = NapCatMessageParser.ParseMessage(
            root,
            selfId,
            _config.DownloadAttachments,
            collectForwards: true);
        var text = parsed.Text;
        // 只有 @ 或引用、没有正文的消息（群里只 @ 一下）同样要唤醒：正文可以为空，
        // 装饰由 MentionsSelf / Quote 表达，宿主据此拼展示行与提示词。
        var decorated = parsed.MentionsSelf || parsed.QuotedId is not null;
        if (string.IsNullOrWhiteSpace(text) && !decorated) return;

        var sender = NapCatMessageParser.GetSenderName(root, userId);
        var messageId = NapCatMessageParser.GetString(root, "message_id");
        var messageType = NapCatMessageParser.GetString(root, "message_type");

        if (messageType == "group")
        {
            var groupId = NapCatMessageParser.GetInt64(root, "group_id");
            // 白名单为空表示不启用白名单，全部放行
            if (groupId <= 0 || (_config.AllowedGroups.Count > 0 && !_config.AllowedGroups.Contains(groupId))) return;

            var conversationId = $"group:{groupId.ToString(CultureInfo.InvariantCulture)}";
            // 历史记录里留下消息 id、纯正文与发送者，回复里的 [quote:...] 与 [at:昵称] 靠它反查；
            // 「@ 了你 / 引用了谁」也是字段（mentionsSelf / quotedId），展示用的
            // 「[会话 id] 发送者（标记）：」只在读取历史时拼
            _store!.RecordMessage(
                conversationId, messageId, text, userId, sender,
                $"群 {groupId.ToString(CultureInfo.InvariantCulture)}",
                parsed.MentionsSelf, parsed.QuotedId);

            // 引用反查、合并转发反查与附件下载都在接收循环之外执行，补完再唤醒桌宠
            // 禁止主动回复的群只是不再按概率随机搭话，@、引用与唤醒词仍然生效
            var wake = parsed.MentionsSelf
                       || ContainsWakeWord(text)
                       || (_config.AllowsProactiveReply(groupId) && RollReplyChance());
            if (parsed.Attachments is not null || parsed.QuotedId is not null || parsed.Forwards is not null)
                _ = FinalizeMessageAsync(
                    conversationId,
                    text,
                    sender,
                    userId,
                    messageId,
                    parsed,
                    wake);
            // 命中唤醒词或概率回复后，群里不 @ 也会唤醒桌宠
            else if (wake)
                RaiseMentioned(conversationId, text, sender, userId, messageId, parsed.MentionsSelf, null);
            return;
        }

        if (messageType == "private")
        {
            // 白名单为空表示不启用白名单，全部放行
            if (_config.AllowedUsers.Count > 0 && !_config.AllowedUsers.Contains(userId)) return;

            var conversationId = $"private:{userId.ToString(CultureInfo.InvariantCulture)}";
            _store!.RecordMessage(
                conversationId, messageId, text, userId, sender, sender, parsed.MentionsSelf, parsed.QuotedId);

            // 唤醒词与概率回复只对群聊生效；私聊是否唤醒只取决于 wakeOnPrivateMessage
            var wake = _config.WakeOnPrivateMessage;
            if (parsed.Attachments is not null || parsed.QuotedId is not null || parsed.Forwards is not null)
                _ = FinalizeMessageAsync(conversationId, text, sender, userId, messageId, parsed, wake);
            else if (wake)
                RaiseMentioned(conversationId, text, sender, userId, messageId, parsed.MentionsSelf, null);
        }
    }

    /// <summary>
    /// 补全一条刚入队的消息：先下载附件并把本地路径写回正文，再反查合并转发内容，最后反查引用内容，
    /// 并按需要唤醒桌宠。异步执行，避免下载/网络等待阻塞接收循环。
    /// </summary>
    private async Task FinalizeMessageAsync(
        string conversationId,
        string body,
        string sender,
        long senderId,
        string? messageId,
        NapCatParsedMessage parsed,
        bool wake)
    {
        try
        {
            if (parsed.Attachments is { Count: > 0 } attachments)
            {
                var hydrated = await _downloader!.HydrateAsync(body, attachments).ConfigureAwait(false);
                if (!string.Equals(hydrated, body, StringComparison.Ordinal))
                {
                    _store!.PatchMessage(conversationId, messageId, body, hydrated);
                    body = hydrated;
                }
            }

            if (parsed.Forwards is { Count: > 0 } forwards)
            {
                var hydrated = await _forwardResolver!.HydrateAsync(body, forwards).ConfigureAwait(false);
                if (!string.Equals(hydrated, body, StringComparison.Ordinal))
                {
                    _store!.PatchMessage(conversationId, messageId, body, hydrated);
                    body = hydrated;
                }
            }
        }
        catch (Exception ex)
        {
            Context.Logger.LogDebug(ex, "处理消息附件或合并转发失败");
        }

        if (parsed.QuotedId is { } quotedId)
        {
            await EnrichQuoteAsync(quotedId, conversationId, body, sender, senderId, messageId, parsed, wake)
                .ConfigureAwait(false);
            return;
        }

        if (wake)
            RaiseMentioned(conversationId, body, sender, senderId, messageId, parsed.MentionsSelf, null);
    }

    /// <summary>
    /// 反查被引用的消息：引用摘要作为结构化字段（<see cref="PlatformIncomingMessage.Quote" />）上报给宿主，
    /// 同时写回本地历史记录（<see cref="NapCatConversationStore.PatchQuote" />）并缓存，
    /// 供之后读历史 / 远端历史时拼成「（引用 发送者：摘要）」。补完再按需要唤醒桌宠。
    /// 反查在接收循环之外执行，避免等待自身响应造成死锁。
    /// </summary>
    private async Task EnrichQuoteAsync(
        string quotedId,
        string conversationId,
        string body,
        string sender,
        long senderId,
        string? messageId,
        NapCatParsedMessage parsed,
        bool wake)
    {
        try
        {
            var quoted = await _quotes!
                .FetchAsync(quotedId, Volatile.Read(ref _selfId))
                .ConfigureAwait(false);

            // 摘要只进字段与历史渲染，正文始终是对方写的内容
            var quote = NapCatQuoteTracker.FormatQuote(quoted);
            if (quote is not null)
            {
                _store!.PatchQuote(conversationId, messageId, quote);
                _quotes.RememberQuote(quotedId, quote);
            }

            // 引用了桌宠自己说的话，等同于在叫桌宠：也算「消息直接指向你」
            var mentionsSelf = parsed.MentionsSelf || quoted?.IsSelf == true;
            if (wake || mentionsSelf)
                RaiseMentioned(conversationId, body, sender, senderId, messageId, mentionsSelf, quote);
        }
        catch (Exception ex)
        {
            Context.Logger.LogDebug(ex, "处理被引用消息 {QuotedId} 失败", quotedId);
            if (wake)
                RaiseMentioned(conversationId, body, sender, senderId, messageId, parsed.MentionsSelf, null);
        }
    }

    /// <summary>消息正文是否含有任一唤醒词（不区分大小写）；未配置唤醒词时始终返回 false。</summary>
    private bool ContainsWakeWord(string text)
    {
        foreach (var word in _config.WakeWords)
        {
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>概率回复（仅群聊生效）：非 @、非引用桌宠、也未命中唤醒词的消息，按配置的概率（0-1）随机命中一次唤醒；0 表示关闭。</summary>
    private bool RollReplyChance()
        => _config.ReplyProbability > 0 && Random.Shared.NextDouble() < _config.ReplyProbability;

    /// <summary>
    /// 唤醒桌宠：上报给宿主的是纯正文，会话 id、发送者昵称与稳定 id 各自单独传，
    /// 「@ 了你」「引用」这类装饰也走字段（<c>MentionsSelf</c> / <c>Quote</c>），不拼进正文：
    /// 宿主据此识别平台命令、做管理员判定，并自己组装展示行与提示词。
    /// </summary>
    private void RaiseMentioned(
        string conversationId,
        string body,
        string sender,
        long senderId,
        string? messageId,
        bool mentionsSelf,
        string? quote)
    {
        var handler = OnMentioned;
        if (handler is null) return;

        try
        {
            handler(this, new PlatformIncomingMessage(
                Id,
                body,
                sender,
                messageId,
                BuildWakeContext(conversationId, body, messageId),
                senderId > 0 ? senderId.ToString(CultureInfo.InvariantCulture) : null,
                conversationId,
                mentionsSelf,
                quote));
        }
        catch (Exception ex)
        {
            Context.Logger.LogWarning(ex, "向宿主转发 NapCat 平台消息失败");
        }
    }

    /// <summary>
    /// 把「桌宠上次回复之后、当前消息之前」的消息作为上下文交给宿主，模型不必再调
    /// get_platform_messages；条数仍受配置上限约束，超出时保留最近的部分。
    /// </summary>
    private string? BuildWakeContext(string conversationId, string body, string? messageId)
    {
        var count = _config.WakeContextMessages;
        if (count <= 0) return null;

        // currentMessageId 由历史存储统一排除：异步补全期间入队的新消息不会混进这条的上下文
        var messages = _store?.GetMessagesSinceLastReply(
            conversationId, count, Volatile.Read(ref _selfId), messageId) ?? [];
        var lines = messages.Select(item => item.FormatLine()).ToList();

        // 没有消息 id 时（戳一戳等）退回按正文去掉当前消息
        if (messageId is not { Length: > 0 } && lines.Count > 0
            && string.Equals(messages[^1].Text, body, StringComparison.Ordinal))
            lines.RemoveAt(lines.Count - 1);

        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    /// <summary>戳一戳命中响应：记入会话历史并唤醒桌宠；自动回戳由响应器自己发送。</summary>
    private void HandlePokeResponse(string conversationId, string body, string sender, long groupId)
    {
        var display = groupId > 0
            ? $"群 {groupId.ToString(CultureInfo.InvariantCulture)}"
            : sender;
        _store?.RecordMessage(conversationId, null, body, 0, sender, display, mentionsSelf: true);
        // 戳一戳本身就是直接冲着桌宠来的
        RaiseMentioned(conversationId, body, sender, 0, null, mentionsSelf: true, quote: null);
    }
}
