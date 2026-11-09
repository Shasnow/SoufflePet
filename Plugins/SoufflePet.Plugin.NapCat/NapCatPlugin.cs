// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SoufflePet.Plugin.Sdk;

namespace SoufflePet.Plugins.NapCat;

/// <summary>
/// NapCat（OneBot 11）消息平台：以正向 WebSocket 客户端连接 NapCat，接收消息事件并用同一连接发送 action。
/// 群聊里 @ 到桌宠、群聊里命中唤醒词（无需 @）、以及允许用户的私聊会通过
/// <see cref="OnMentioned" /> 主动唤醒桌宠；还可为群聊配置概率回复，让非 @、非引用、未命中唤醒词的消息也按概率随机唤醒。
/// 插件始终忽略桌宠自己发出的消息，避免自己回复自己。
/// 支持按会话 id（group:群号 / private:QQ号）读写指定会话的消息。
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

    /// <summary>机器人自己的 QQ 号，来自事件里的 self_id；用于识别 @ 与过滤自己的消息。</summary>
    private long _selfId;

    /// <summary>平台 id，供工具的 platform 参数使用。</summary>
    public string Id => "napcat";

    /// <summary>平台角色说明，交给宿主注入系统提示词；未配置时返回 null（不注入）。</summary>
    public string? RolePrompt => _config.RolePrompt.Length == 0 ? null : _config.RolePrompt;

    /// <summary>收到需要桌宠处理的消息时触发，由宿主订阅后唤醒桌宠。</summary>
    public event EventHandler<PlatformIncomingMessage>? OnMentioned;

    protected override Task OnInitializedAsync(CancellationToken cancellationToken)
    {
        _config = NapCatConfig.Load(Context.Configuration, Context.PluginDirectory);

        _store = new NapCatConversationStore(Context.Logger, Context.DataDirectory);
        _store.Load();
        _store.StartAutoSave();

        _connection = new NapCatConnection(Context.Logger, _config.AccessToken, HandleMessage);
        _quotes = new NapCatQuoteTracker(Context.Logger, _connection);
        _sender = new NapCatSender(Context.Logger, _connection, _quotes);
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
            "NapCat 平台将连接 {Url}；私聊白名单 {Users} 个，群白名单 {Groups} 个",
            _config.WsUrl,
            _config.AllowedUsers.Count,
            _config.AllowedGroups.Count);

        _connection.Start(uri);
        return Task.CompletedTask;
    }

    /// <summary>向指定会话发送消息；conversationId 形如 group:12345 或 private:777。</summary>
    public async Task SendMessageAsync(string conversationId, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        if (!NapCatConversationId.TryParse(conversationId, out var conversation))
            throw new ArgumentException(
                $"无法识别的会话 id「{conversationId}」，应形如 group:12345 或 private:777。",
                nameof(conversationId));

        await _sender!.SendMessageAsync(conversation, message, _config.MaxReplyChars).ConfigureAwait(false);
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
                    _quotes!.IsSentBySelf,
                    collectForwards: true);
                var text = parsed.Text;
                if (string.IsNullOrWhiteSpace(text)) continue;

                // 反查过的引用直接用缓存里的摘要替换占位，没缓存过就保留「（引用）」
                if (parsed.QuotedId is { } quotedHistoryId && _quotes.TryGetQuoteLabel(quotedHistoryId) is { } quotedLabel)
                    text = text.Replace(NapCatMessageParser.QuotePlaceholder, quotedLabel, StringComparison.Ordinal);

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
                lines.Add($"[{id}] {sender}：{text}");
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
            _quotes!.IsSentBySelf,
            _config.DownloadAttachments,
            collectForwards: true);
        var text = parsed.Text;
        if (string.IsNullOrWhiteSpace(text)) return;

        var sender = NapCatMessageParser.GetSenderName(root, userId);
        var messageId = NapCatMessageParser.GetString(root, "message_id");
        var messageType = NapCatMessageParser.GetString(root, "message_type");

        if (messageType == "group")
        {
            var groupId = NapCatMessageParser.GetInt64(root, "group_id");
            // 白名单为空表示不启用白名单，全部放行
            if (groupId <= 0 || (_config.AllowedGroups.Count > 0 && !_config.AllowedGroups.Contains(groupId))) return;

            var conversationId = $"group:{groupId.ToString(CultureInfo.InvariantCulture)}";
            var line = $"[{conversationId}] {sender}：{text}";
            _store!.RecordMessage(conversationId, line, $"群 {groupId.ToString(CultureInfo.InvariantCulture)}");

            // 引用反查、合并转发反查与附件下载都在接收循环之外执行，补完再唤醒桌宠
            var wake = parsed.MentionsSelf || ContainsWakeWord(text) || RollReplyChance();
            if (parsed.Attachments is not null || parsed.QuotedId is not null || parsed.Forwards is not null)
                _ = FinalizeMessageAsync(
                    conversationId,
                    line,
                    sender,
                    messageId,
                    parsed,
                    wake);
            // 命中唤醒词或概率回复后，群里不 @ 也会唤醒桌宠
            else if (wake)
                RaiseMentioned(line, sender, messageId);
            return;
        }

        if (messageType == "private")
        {
            // 白名单为空表示不启用白名单，全部放行
            if (_config.AllowedUsers.Count > 0 && !_config.AllowedUsers.Contains(userId)) return;

            var conversationId = $"private:{userId.ToString(CultureInfo.InvariantCulture)}";
            var line = $"[{conversationId}] {sender}：{text}";
            _store!.RecordMessage(conversationId, line, sender);

            // 唤醒词与概率回复只对群聊生效；私聊是否唤醒只取决于 wakeOnPrivateMessage
            var wake = _config.WakeOnPrivateMessage;
            if (parsed.Attachments is not null || parsed.QuotedId is not null || parsed.Forwards is not null)
                _ = FinalizeMessageAsync(conversationId, line, sender, messageId, parsed, wake);
            else if (wake)
                RaiseMentioned(line, sender, messageId);
        }
    }

    /// <summary>
    /// 补全一条刚入队的消息：先下载附件并把本地路径写回正文，再反查合并转发内容，最后反查引用内容，
    /// 并按需要唤醒桌宠。异步执行，避免下载/网络等待阻塞接收循环。
    /// </summary>
    private async Task FinalizeMessageAsync(
        string conversationId,
        string line,
        string sender,
        string? messageId,
        NapCatParsedMessage parsed,
        bool wake)
    {
        try
        {
            if (parsed.Attachments is { Count: > 0 } attachments)
            {
                var hydrated = await _downloader!.HydrateAsync(line, attachments).ConfigureAwait(false);
                if (!string.Equals(hydrated, line, StringComparison.Ordinal))
                {
                    _store!.PatchRecordedLine(conversationId, line, hydrated);
                    line = hydrated;
                }
            }

            if (parsed.Forwards is { Count: > 0 } forwards)
            {
                var hydrated = await _forwardResolver!.HydrateAsync(line, forwards).ConfigureAwait(false);
                if (!string.Equals(hydrated, line, StringComparison.Ordinal))
                {
                    _store!.PatchRecordedLine(conversationId, line, hydrated);
                    line = hydrated;
                }
            }
        }
        catch (Exception ex)
        {
            Context.Logger.LogDebug(ex, "处理消息附件或合并转发失败");
        }

        if (parsed.QuotedId is { } quotedId)
        {
            await EnrichQuoteAsync(quotedId, conversationId, line, sender, messageId, wake).ConfigureAwait(false);
            return;
        }

        if (wake) RaiseMentioned(line, sender, messageId);
    }

    /// <summary>
    /// 反查被引用的消息，把它的正文补进这一行：成功时写成「（引用 发送者：摘要）」，
    /// 失败时退回「[引用你的消息]」标记。补完再按需要唤醒桌宠，保证唤醒文本里带得上引用内容。
    /// 反查在接收循环之外执行，避免等待自身响应造成死锁。
    /// </summary>
    private async Task EnrichQuoteAsync(
        string quotedId,
        string conversationId,
        string line,
        string sender,
        string? messageId,
        bool wake)
    {
        try
        {
            var quoted = await _quotes!.FetchAsync(quotedId, Volatile.Read(ref _selfId)).ConfigureAwait(false);
            var enriched = _quotes.BuildEnrichedLine(quotedId, line, quoted);
            if (!string.Equals(enriched, line, StringComparison.Ordinal))
                _store!.PatchRecordedLine(conversationId, line, enriched);

            if (wake || quoted?.IsSelf == true) RaiseMentioned(enriched, sender, messageId);
        }
        catch (Exception ex)
        {
            Context.Logger.LogDebug(ex, "处理被引用消息 {QuotedId} 失败", quotedId);
            if (wake) RaiseMentioned(line, sender, messageId);
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

    private void RaiseMentioned(string content, string sender, string? messageId)
    {
        var handler = OnMentioned;
        if (handler is null) return;

        try
        {
            handler(this, new PlatformIncomingMessage(Id, content, sender, messageId));
        }
        catch (Exception ex)
        {
            Context.Logger.LogWarning(ex, "向宿主转发 NapCat 平台消息失败");
        }
    }
}
