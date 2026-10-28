// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SoufflePet.Plugin.Sdk;

namespace SoufflePet.Plugins.NapCat;

/// <summary>
/// NapCat（OneBot 11）消息平台：以正向 WebSocket 客户端连接 NapCat，接收消息事件并用同一连接发送 action。
/// 群聊里 @ 到桌宠（或按配置关掉 @ 限制后任意群消息）、群聊里命中唤醒词（无需 @）、以及允许用户的私聊会通过
/// <see cref="OnMentioned" /> 主动唤醒桌宠；还可为群聊配置概率回复，让非 @、非引用、未命中唤醒词的消息也按概率随机唤醒。
/// 支持按会话 id（group:群号 / private:QQ号）读写指定会话的消息。
/// </summary>
public sealed class NapCatPlugin : PluginBase, IMessagePlatform
{
    /// <summary>每个会话最多保留的历史消息条数，供按会话读取使用。</summary>
    private const int MaxHistoryMessages = 50;

    /// <summary>最多保留的会话数，超出时淘汰最久未活跃的会话。</summary>
    private const int MaxConversations = 200;

    /// <summary>引用消息里展示的被引用正文最大字数。</summary>
    private const int QuotedSummaryLength = 60;

    /// <summary>解析阶段给引用段留的占位，反查拿到正文后原地替换。</summary>
    private const string QuotePlaceholder = "（引用）";

    /// <summary>记住的自身发送消息 id 数量上限，用于识别他人引用桌宠消息。</summary>
    private const int MaxSentMessageIds = 200;

    /// <summary>缓存的反查引用摘要条数上限。</summary>
    private const int MaxQuoteLabels = 200;

    /// <summary>发送 action 后等待 NapCat 响应的超时。</summary>
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(10);

    /// <summary>断线后的重连间隔。</summary>
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    /// <summary>单条 WebSocket 消息的长度上限，防止异常数据无限占用内存。</summary>
    private const int MaxPayloadBytes = 8 * 1024 * 1024;

    /// <summary>单个附件允许下载的最大字节数。</summary>
    private const long MaxAttachmentBytes = 20 * 1024 * 1024;

    /// <summary>图片作为消息段发送时的最大字节数（base64 编码会膨胀）。</summary>
    private const long MaxSendImageBytes = 5L * 1024 * 1024;

    /// <summary>文件走 base64 上传的上限；更大的文件传本地绝对路径，避免内存与 WebSocket 压力。</summary>
    private const long MaxBase64FileBytes = 10L * 1024 * 1024;

    /// <summary>单个附件的下载超时。</summary>
    private static readonly TimeSpan AttachmentDownloadTimeout = TimeSpan.FromSeconds(60);

    /// <summary>下载附件用的 HTTP 客户端（插件内共享）。</summary>
    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>创建下载用的 HttpClient：QQ 多媒体 CDN 对没有 UA 的请求会长时间不响应，且本机 IPv6 不通时 .NET 会卡到超时。</summary>
    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            ConnectCallback = ConnectIpv4Async,
        };

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) SoufflePet/1.0");
        return client;
    }

    /// <summary>只用 IPv4 建连：本机 IPv6 出口不通，.NET 默认优先尝试 IPv6 会把请求卡到超时。</summary>
    private static async ValueTask<Stream> ConnectIpv4Async(
        SocketsHttpConnectionContext context,
        CancellationToken token)
    {
        var addresses = await Dns
            .GetHostAddressesAsync(context.DnsEndPoint.Host, token)
            .ConfigureAwait(false);

        var address = Array.Find(addresses, item => item.AddressFamily == AddressFamily.InterNetwork)
                      ?? (addresses.Length > 0
                          ? addresses[0]
                          : throw new HttpRequestException($"无法解析主机 {context.DnsEndPoint.Host}"));

        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket
                .ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token)
                .ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>消息字符串里 OneBot 的 CQ 码，用于解析 message_format=string 的平台。</summary>
    private static readonly Regex CqCodePattern = new(@"\[CQ:([A-Za-z_]+)((?:,[^\]]*)?)\]", RegexOptions.Compiled);

    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Lock _sync = new();
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _history = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _conversationNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastActive = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _sentMessageIds = new();
    private readonly HashSet<string> _sentMessageIdSet = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _quoteLabels = new(StringComparer.Ordinal);
    private readonly Queue<string> _quoteLabelOrder = new();
    private readonly CancellationTokenSource _cts = new();

    private ClientWebSocket? _socket;
    private Task? _pump;

    private string _wsUrl = string.Empty;
    private string _accessToken = string.Empty;
    private HashSet<long> _allowedUsers = [];
    private HashSet<long> _allowedGroups = [];
    private bool _requireAtInGroup = true;
    private bool _wakeOnPrivateMessage = true;

    /// <summary>唤醒词（仅群聊生效）：消息正文只要含有其中任意一个（不区分大小写）就唤醒桌宠（无需 @）；为空表示不启用。</summary>
    private string[] _wakeWords = [];

    /// <summary>是否过滤机器人自己发出的消息，默认关闭。</summary>
    private bool _filterSelfMessage;
    private int _maxReplyChars = 1500;

    /// <summary>概率回复（仅群聊生效）：非 @、非引用桌宠、也未命中唤醒词的消息，按此概率（0-1）随机唤醒桌宠；0 表示关闭。</summary>
    private double _replyProbability;

    /// <summary>是否把收到的图片/文件下载到本地，并把路径写进消息。</summary>
    private bool _downloadAttachments = true;

    /// <summary>附件缓存目录（绝对路径）。</summary>
    private string _attachmentCacheDir = string.Empty;

    /// <summary>机器人自己的 QQ 号，来自事件里的 self_id；用于识别 @ 与过滤自己的消息。</summary>
    private long _selfId;

    /// <summary>平台角色说明（例如「你正在托管主人的 QQ」），注入系统提示词；为空则不注入。</summary>
    private string _rolePrompt = string.Empty;

    /// <summary>平台 id，供工具的 platform 参数使用。</summary>
    public string Id => "napcat";

    /// <summary>平台角色说明，交给宿主注入系统提示词；未配置时返回 null（不注入）。</summary>
    public string? RolePrompt => _rolePrompt.Length == 0 ? null : _rolePrompt;

    /// <summary>收到需要桌宠处理的消息时触发，由宿主订阅后唤醒桌宠。</summary>
    public event EventHandler<PlatformIncomingMessage>? OnMentioned;

    protected override Task OnInitializedAsync(CancellationToken cancellationToken)
    {
        ReadConfiguration();

        if (!Uri.TryCreate(_wsUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "ws" && uri.Scheme != "wss"))
        {
            Context.Logger.LogWarning("NapCat WebSocket 地址无效：{Url}，插件不会发起连接", _wsUrl);
            return Task.CompletedTask;
        }

        Context.Logger.LogInformation(
            "NapCat 平台将连接 {Url}；私聊白名单 {Users} 个，群白名单 {Groups} 个",
            _wsUrl,
            _allowedUsers.Count,
            _allowedGroups.Count);

        _pump = Task.Run(() => RunAsync(uri, _cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>向指定会话发送消息；conversationId 形如 group:12345 或 private:777。</summary>
    public async Task SendMessageAsync(string conversationId, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        if (!TryParseConversationId(conversationId, out var conversation))
            throw new ArgumentException(
                $"无法识别的会话 id「{conversationId}」，应形如 group:12345 或 private:777。",
                nameof(conversationId));

        await SendToConversationAsync(conversation, message).ConfigureAwait(false);
    }

    /// <summary>向指定会话发送图片或文件；conversationId 形如 group:12345 或 private:777。</summary>
    public async Task SendAttachmentAsync(string conversationId, PlatformAttachment attachment)
    {
        if (!TryParseConversationId(conversationId, out var conversation))
            throw new ArgumentException(
                $"无法识别的会话 id「{conversationId}」，应形如 group:12345 或 private:777。",
                nameof(conversationId));

        await SendAttachmentToConversationAsync(conversation, attachment).ConfigureAwait(false);
    }

    /// <summary>
    /// 发送附件：图片走消息段（base64），文件走 upload_group_file / upload_private_file；
    /// 小文件用 base64 避免路径权限差异，大文件保留绝对路径减少内存占用。
    /// </summary>
    private async Task SendAttachmentToConversationAsync(
        (string Kind, long Id) conversation,
        PlatformAttachment attachment)
    {
        var path = Path.GetFullPath(attachment.FilePath);
        if (!File.Exists(path))
            throw new InvalidOperationException($"文件不存在：{path}");

        var name = string.IsNullOrWhiteSpace(attachment.DisplayName)
            ? Path.GetFileName(path)
            : attachment.DisplayName.Trim();

        ClientWebSocket? socket;
        lock (_sync) socket = _socket;

        if (socket is not { State: WebSocketState.Open })
            throw new InvalidOperationException("NapCat WebSocket 未连接，附件没有发送。");

        var isGroup = conversation.Kind == "group";
        if (attachment.Kind == PlatformAttachmentKind.Image)
        {
            await SendImageAsync(socket, conversation, path, name).ConfigureAwait(false);
        }
        else
        {
            await SendFileAsync(socket, conversation, path, name).ConfigureAwait(false);
        }

        Context.Logger.LogInformation(
            "已向{Kind} {Id} 发送{Label}「{Name}」",
            isGroup ? "群" : "用户",
            conversation.Id,
            attachment.Kind == PlatformAttachmentKind.Image ? "图片" : "文件",
            name);
    }

    /// <summary>图片按 OneBot 消息段发送：数组格式避免 CQ 码转义，base64 不依赖 NapCat 的文件权限。</summary>
    private async Task SendImageAsync(
        ClientWebSocket socket,
        (string Kind, long Id) conversation,
        string path,
        string name)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxSendImageBytes)
            throw new InvalidOperationException(
                $"图片「{name}」大小为 {info.Length} 字节，超过 {MaxSendImageBytes} 的发送上限。");

        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        var parameters = BuildMessageParameters(
            conversation,
            new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "image",
                    ["data"] = new Dictionary<string, object?>
                    {
                        ["file"] = "base64://" + Convert.ToBase64String(bytes),
                    },
                },
            });

        var response = await SendActionAsync(
                socket,
                conversation.Kind == "group" ? "send_group_msg" : "send_private_msg",
                parameters)
            .ConfigureAwait(false);
        RememberSentMessageId(GetSentMessageId(response));
    }

    /// <summary>文件走平台上传接口；小文件用 base64，大文件传绝对路径。</summary>
    private async Task SendFileAsync(
        ClientWebSocket socket,
        (string Kind, long Id) conversation,
        string path,
        string name)
    {
        var info = new FileInfo(path);
        var file = info.Length <= MaxBase64FileBytes
            ? "base64://" + Convert.ToBase64String(await File.ReadAllBytesAsync(path).ConfigureAwait(false))
            : path;

        var isGroup = conversation.Kind == "group";
        var parameters = new Dictionary<string, object?>
        {
            [isGroup ? "group_id" : "user_id"] = conversation.Id,
            ["file"] = file,
            ["name"] = name,
        };

        var response = await SendActionAsync(
                socket,
                isGroup ? "upload_group_file" : "upload_private_file",
                parameters)
            .ConfigureAwait(false);
        RememberSentMessageId(GetSentMessageId(response));
    }

    /// <summary>构造附件消息参数：附件用消息段数组，不设置 auto_escape。</summary>
    private static Dictionary<string, object?> BuildMessageParameters(
        (string Kind, long Id) conversation,
        object message) => new()
    {
        [conversation.Kind == "group" ? "group_id" : "user_id"] = conversation.Id,
        ["message"] = message,
    };

    /// <summary>从发送响应里取 message_id；没有时返回 null。</summary>
    private static string? GetSentMessageId(JsonElement response) =>
        response.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? GetString(data, "message_id")
            : null;

    /// <summary>列出本次运行期间收到过消息的会话，按最近活跃排序。</summary>
    public Task<IReadOnlyList<PlatformConversation>> GetConversationsAsync()
    {
        lock (_sync)
        {
            IReadOnlyList<PlatformConversation> list = _history
                .Where(pair => pair.Value.Count > 0)
                .OrderByDescending(pair =>
                    _lastActive.TryGetValue(pair.Key, out var time) ? time : DateTimeOffset.MinValue)
                .Select(pair => new PlatformConversation(pair.Key, _conversationNames.GetValueOrDefault(pair.Key)))
                .ToList();
            return Task.FromResult(list);
        }
    }

    /// <summary>
    /// 读取指定会话最近的消息：优先向 NapCat 查询历史（含插件启动前的消息），
    /// 未连接或查询失败时退回本次运行缓存。
    /// </summary>
    public async Task<string> GetMessagesAsync(string conversationId, int limit = 20)
    {
        var id = NormalizeConversationId(conversationId);
        if (id.Length == 0 || !TryParseConversationId(id, out var conversation)) return string.Empty;

        var count = Math.Clamp(limit, 1, MaxHistoryMessages);
        var remote = await FetchHistoryAsync(conversation, count).ConfigureAwait(false);

        lock (_sync)
        {
            if (remote.Count > 0) return string.Join('\n', remote);
            if (!_history.TryGetValue(id, out var history) || history.Count == 0) return string.Empty;

            return string.Join('\n', history.Skip(Math.Max(0, history.Count - count)));
        }
    }

    /// <summary>
    /// 通过 OneBot 历史接口拉取会话最近的消息；未连接、接口不支持或返回为空时返回空列表，
    /// 由调用方退回本地缓存。
    /// </summary>
    private async Task<IReadOnlyList<string>> FetchHistoryAsync(
        (string Kind, long Id) conversation,
        int count)
    {
        ClientWebSocket? socket;
        lock (_sync) socket = _socket;
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
            var response = await SendActionAsync(
                    socket,
                    isGroup ? "get_group_msg_history" : "get_friend_msg_history",
                    parameters)
                .ConfigureAwait(false);

            if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return [];
            if (!data.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                return [];

            var lines = new List<string>();
            foreach (var message in messages.EnumerateArray())
            {
                if (message.ValueKind != JsonValueKind.Object) continue;

                var parsed = ParseMessage(message);
                var text = parsed.Text;
                if (string.IsNullOrWhiteSpace(text)) continue;

                // 反查过的引用直接用缓存里的摘要替换占位，没缓存过就保留「（引用）」
                if (parsed.QuotedId is { } quotedHistoryId && TryGetQuoteLabel(quotedHistoryId) is { } quotedLabel)
                    text = text.Replace(QuotePlaceholder, quotedLabel, StringComparison.Ordinal);

                var sender = GetSenderName(message, GetInt64(message, "user_id"));
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

    private async Task SendToConversationAsync((string Kind, long Id) conversation, string message)
    {
        ClientWebSocket? socket;
        lock (_sync) socket = _socket;

        if (socket is not { State: WebSocketState.Open })
            throw new InvalidOperationException("NapCat WebSocket 未连接，消息没有发送。");

        var isGroup = conversation.Kind == "group";
        foreach (var chunk in SplitMessage(message, _maxReplyChars))
        {
            var parameters = isGroup
                ? new Dictionary<string, object?>
                {
                    ["group_id"] = conversation.Id,
                    ["message"] = chunk,
                    ["auto_escape"] = true,
                }
                : new Dictionary<string, object?>
                {
                    ["user_id"] = conversation.Id,
                    ["message"] = chunk,
                    ["auto_escape"] = true,
                };

            var response = await SendActionAsync(
                    socket,
                    isGroup ? "send_group_msg" : "send_private_msg",
                    parameters)
                .ConfigureAwait(false);

            // 记住自己发出的消息 id，供之后快速识别「他人引用桌宠消息」
            var sentId = response.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                ? GetString(data, "message_id")
                : null;
            if (sentId is null)
                Context.Logger.LogDebug("NapCat 发送响应缺少 message_id：{Raw}", response.GetRawText());
            RememberSentMessageId(sentId);
        }

        Context.Logger.LogInformation("已向{Kind} {Id} 发送消息", isGroup ? "群" : "用户", conversation.Id);
    }

    public override async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放过，忽略
        }

        ClientWebSocket? socket;
        lock (_sync) socket = _socket;
        if (socket is { State: WebSocketState.Open })
        {
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "shutdown", closeTimeout.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Context.Logger.LogDebug(ex, "关闭 NapCat WebSocket 时出错");
            }
        }

        if (_pump is { } pump)
        {
            try
            {
                await pump.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Context.Logger.LogDebug(ex, "等待 NapCat 连接循环退出时超时");
            }
        }

        List<TaskCompletionSource<JsonElement>> pending;
        lock (_sync)
        {
            pending = [.. _pending.Values];
            _pending.Clear();
        }

        foreach (var completion in pending) completion.TrySetCanceled();

        _cts.Dispose();
        try
        {
            _sendGate.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // 已有发送操作释放过，忽略
        }
    }

    /// <summary>连接循环：连上后持续接收事件，断开或异常时等待一段时间再重连。</summary>
    private async Task RunAsync(Uri uri, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ClientWebSocket? socket = null;
            try
            {
                socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                if (!string.IsNullOrWhiteSpace(_accessToken))
                    socket.Options.SetRequestHeader("Authorization", $"Bearer {_accessToken}");

                await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
                lock (_sync) _socket = socket;
                Context.Logger.LogInformation("NapCat WebSocket 已连接：{Url}", uri);

                await PumpAsync(socket, cancellationToken).ConfigureAwait(false);
                Context.Logger.LogWarning("NapCat WebSocket 已断开");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Context.Logger.LogWarning(ex, "NapCat WebSocket 连接异常");
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_socket, socket)) _socket = null;
                }

                socket?.Dispose();
            }

            try
            {
                await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>读取 WebSocket 帧：把分片拼成完整 JSON，再分派给事件或 action 响应。</summary>
    private async Task PumpAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var payload = new MemoryStream();

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                Context.Logger.LogInformation(
                    "NapCat 关闭了连接：{Status} {Reason}",
                    result.CloseStatus,
                    result.CloseStatusDescription);
                return;
            }

            payload.Write(buffer, 0, result.Count);
            if (payload.Length > MaxPayloadBytes)
            {
                Context.Logger.LogWarning("NapCat 推送的单条数据超过 {Size} 字节，已丢弃", MaxPayloadBytes);
                payload.SetLength(0);
                continue;
            }

            if (!result.EndOfMessage) continue;

            var json = Encoding.UTF8.GetString(payload.GetBuffer(), 0, (int)payload.Length);
            payload.SetLength(0);
            HandlePayload(json);
        }
    }

    private void HandlePayload(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            // 先看 echo：这是本插件发出的 action 的响应
            if (root.TryGetProperty("echo", out var echo) && echo.ValueKind == JsonValueKind.String)
            {
                CompletePending(echo.GetString()!, root);
                return;
            }

            if (GetString(root, "post_type") != "message") return;
            HandleMessage(root);
        }
        catch (JsonException ex)
        {
            Context.Logger.LogWarning(ex, "解析 NapCat 推送内容失败");
        }
    }

    private void HandleMessage(JsonElement root)
    {
        var selfId = GetInt64(root, "self_id");
        if (selfId > 0) _selfId = selfId;

        var userId = GetInt64(root, "user_id");
        if (userId <= 0) return;
        if (_filterSelfMessage && userId == _selfId) return;

        var parsed = ParseMessage(root, _downloadAttachments);
        var text = parsed.Text;
        if (string.IsNullOrWhiteSpace(text)) return;

        var sender = GetSenderName(root, userId);
        var messageId = GetString(root, "message_id");
        var messageType = GetString(root, "message_type");

        if (messageType == "group")
        {
            var groupId = GetInt64(root, "group_id");
            // 白名单为空表示不启用白名单，全部放行
            if (groupId <= 0 || (_allowedGroups.Count > 0 && !_allowedGroups.Contains(groupId))) return;

            var conversationId = $"group:{groupId.ToString(CultureInfo.InvariantCulture)}";
            var line = $"[{conversationId}] {sender}：{text}";
            RecordMessage(conversationId, line, $"群 {groupId.ToString(CultureInfo.InvariantCulture)}");

            // 引用反查与附件下载都在接收循环之外执行，补完再唤醒桌宠
            var wake = parsed.MentionsSelf || ContainsWakeWord(text) || !_requireAtInGroup || RollReplyChance();
            if (parsed.Attachments is not null || parsed.QuotedId is not null)
                _ = FinalizeMessageAsync(
                    conversationId,
                    line,
                    sender,
                    messageId,
                    parsed,
                    wake);
            // 命中唤醒词、关掉 @ 限制或命中概率回复后，群里不 @ 也会唤醒桌宠
            else if (wake)
                RaiseMentioned(line, sender, messageId);
            return;
        }

        if (messageType == "private")
        {
            // 白名单为空表示不启用白名单，全部放行
            if (_allowedUsers.Count > 0 && !_allowedUsers.Contains(userId)) return;

            var conversationId = $"private:{userId.ToString(CultureInfo.InvariantCulture)}";
            var line = $"[{conversationId}] {sender}：{text}";
            RecordMessage(conversationId, line, sender);

            // 唤醒词与概率回复只对群聊生效；私聊是否唤醒只取决于 wakeOnPrivateMessage
            var wake = _wakeOnPrivateMessage;
            if (parsed.Attachments is not null || parsed.QuotedId is not null)
                _ = FinalizeMessageAsync(conversationId, line, sender, messageId, parsed, wake);
            else if (wake)
                RaiseMentioned(line, sender, messageId);
        }
    }

    /// <summary>
    /// 补全一条刚入队的消息：先下载附件并把本地路径写回正文，再反查引用内容，
    /// 最后按需要唤醒桌宠。异步执行，避免下载/网络等待阻塞接收循环。
    /// </summary>
    private async Task FinalizeMessageAsync(
        string conversationId,
        string line,
        string sender,
        string? messageId,
        ParsedMessage parsed,
        bool wake)
    {
        try
        {
            if (parsed.Attachments is { Count: > 0 } attachments)
            {
                var hydrated = await HydrateAttachmentsAsync(line, attachments).ConfigureAwait(false);
                if (!string.Equals(hydrated, line, StringComparison.Ordinal))
                {
                    PatchRecordedLine(conversationId, line, hydrated);
                    line = hydrated;
                }
            }
        }
        catch (Exception ex)
        {
            Context.Logger.LogDebug(ex, "处理消息附件失败");
        }

        if (parsed.QuotedId is { } quotedId)
        {
            await EnrichQuoteAsync(quotedId, conversationId, line, sender, messageId, wake).ConfigureAwait(false);
            return;
        }

        if (wake) RaiseMentioned(line, sender, messageId);
    }

    /// <summary>解析出的消息正文、是否 @/引用桌宠、首条被引用消息 id，以及待下载的附件。</summary>
    private sealed record ParsedMessage(
        string Text,
        bool MentionsSelf,
        string? QuotedId,
        IReadOnlyList<PendingAttachment>? Attachments = null);

    /// <summary>正文里的附件占位符、下载地址，以及下载失败时退回的文本。</summary>
    private sealed record PendingAttachment(
        string Placeholder,
        string Url,
        string Label,
        string Prefix,
        string? FileName,
        string Fallback);

    /// <summary>解析消息正文：支持 OneBot 的数组段格式，也兼容 string + CQ 码格式。</summary>
    private ParsedMessage ParseMessage(JsonElement root, bool collectAttachments = false)
    {
        if (root.TryGetProperty("message", out var message))
        {
            if (message.ValueKind == JsonValueKind.Array) return ParseSegments(message, collectAttachments);
            if (message.ValueKind == JsonValueKind.String)
                return ParseCqCode(message.GetString() ?? string.Empty, collectAttachments);
        }

        return ParseCqCode(GetString(root, "raw_message") ?? string.Empty, collectAttachments);
    }

    private ParsedMessage ParseSegments(JsonElement segments, bool collectAttachments)
    {
        var builder = new StringBuilder();
        var mentionsSelf = false;
        string? quotedId = null;
        List<PendingAttachment>? attachments = collectAttachments ? [] : null;

        foreach (var segment in segments.EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Object) continue;
            if (!segment.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;

            switch (GetString(segment, "type"))
            {
                case "reply":
                    // 引用的消息是桌宠自己发出的，同样视为在叫桌宠；正文内容稍后用 get_msg 反查补上
                    var id = GetString(data, "id") ?? GetString(data, "message_id");
                    if (string.IsNullOrWhiteSpace(id)) break;

                    // 先用占位表示这是一条引用，读历史时也能看出回复关系
                    builder.Append(QuotePlaceholder);
                    quotedId ??= id;
                    if (IsSentBySelf(id)) mentionsSelf = true;

                    break;

                case "text":
                    builder.Append(GetString(data, "text"));
                    break;

                case "at":
                    var qq = GetString(data, "qq");
                    if (qq == "all")
                    {
                        builder.Append("@全体成员");
                    }
                    else if (qq == _selfId.ToString(CultureInfo.InvariantCulture))
                    {
                        mentionsSelf = true;
                        builder.Append("@你");
                    }
                    else if (!string.IsNullOrEmpty(qq))
                    {
                        builder.Append('@').Append(qq);
                    }

                    break;

                case "image":
                    AppendAttachment(builder, attachments, "图片", "image",
                        GetString(data, "url"), GetString(data, "file"), "[图片]");
                    break;
                case "face":
                    builder.Append("[表情]");
                    break;
                case "record":
                    builder.Append("[语音]");
                    break;
                case "video":
                    builder.Append("[视频]");
                    break;
                case "file":
                    // 文件消息会把文件名放在 name/file 里，带上名字才知道对方发了什么
                    var fileName = GetString(data, "name") ?? GetString(data, "file");
                    AppendAttachment(builder, attachments, "文件", "file",
                        GetString(data, "url"), fileName, FormatFileSegment(fileName));
                    break;
                case "forward":
                    builder.Append("[合并转发]");
                    break;
                case "json":
                case "xml":
                    builder.Append("[卡片消息]");
                    break;
            }
        }

        return new ParsedMessage(
            builder.ToString().Trim(),
            mentionsSelf,
            quotedId,
            attachments is { Count: > 0 } ? attachments : null);
    }

    /// <summary>解析 CQ 码字符串；非文本段替换成可读占位，@ 自己时记录提醒标记。</summary>
    private ParsedMessage ParseCqCode(string raw, bool collectAttachments)
    {
        var builder = new StringBuilder();
        var mentionsSelf = false;
        string? quotedId = null;
        List<PendingAttachment>? attachments = collectAttachments ? [] : null;
        var index = 0;

        foreach (Match match in CqCodePattern.Matches(raw))
        {
            builder.Append(UnescapeCqText(raw[index..match.Index]));
            index = match.Index + match.Length;

            var type = match.Groups[1].Value;
            var arguments = match.Groups[2].Value;
            switch (type)
            {
                case "reply":
                    var id = FindCqArgument(arguments, "id");
                    if (string.IsNullOrWhiteSpace(id)) break;

                    builder.Append(QuotePlaceholder);
                    quotedId ??= id;
                    if (IsSentBySelf(id)) mentionsSelf = true;

                    break;

                case "at":
                    var qq = FindCqArgument(arguments, "qq");
                    if (qq == "all")
                    {
                        builder.Append("@全体成员");
                    }
                    else if (qq == _selfId.ToString(CultureInfo.InvariantCulture))
                    {
                        mentionsSelf = true;
                        builder.Append("@你");
                    }
                    else if (!string.IsNullOrEmpty(qq))
                    {
                        builder.Append('@').Append(qq);
                    }

                    break;

                case "image":
                    AppendAttachment(builder, attachments, "图片", "image",
                        FindCqArgument(arguments, "url"),
                        FindCqArgument(arguments, "file"),
                        "[图片]");
                    break;
                case "face":
                    builder.Append("[表情]");
                    break;
                case "record":
                    builder.Append("[语音]");
                    break;
                case "video":
                    builder.Append("[视频]");
                    break;
                case "file":
                    var fileName = FindCqArgument(arguments, "name") ?? FindCqArgument(arguments, "file");
                    AppendAttachment(builder, attachments, "文件", "file",
                        FindCqArgument(arguments, "url"), fileName, FormatFileSegment(fileName));
                    break;
                case "forward":
                    builder.Append("[合并转发]");
                    break;
                case "json":
                case "xml":
                    builder.Append("[卡片消息]");
                    break;
            }
        }

        builder.Append(UnescapeCqText(raw[index..]));
        return new ParsedMessage(
            builder.ToString().Trim(),
            mentionsSelf,
            quotedId,
            attachments is { Count: > 0 } ? attachments : null);
    }

    /// <summary>
    /// 追加一个图片/文件段：可下载时写入占位符并登记下载任务，否则直接写可读占位。
    /// </summary>
    private static void AppendAttachment(
        StringBuilder builder,
        List<PendingAttachment>? attachments,
        string label,
        string prefix,
        string? url,
        string? fileName,
        string fallback)
    {
        var downloadUrl = attachments is null ? null : ResolveDownloadUrl(url);
        if (downloadUrl is null)
        {
            builder.Append(fallback);
            return;
        }

        var placeholder = $"[{label}#{attachments!.Count + 1}]";
        attachments.Add(new PendingAttachment(placeholder, downloadUrl, label, prefix, fileName, fallback));
        builder.Append(placeholder);
    }

    /// <summary>只接受 http/https 的附件地址；其它情况返回 null。</summary>
    private static string? ResolveDownloadUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsoluteUri
            : null;
    }

    /// <summary>把正文里的附件占位符替换成「[图片 本地路径]」；下载失败时退回「[图片]」。</summary>
    private async Task<string> HydrateAttachmentsAsync(string line, IReadOnlyList<PendingAttachment> attachments)
    {
        foreach (var attachment in attachments)
        {
            var path = await DownloadAttachmentAsync(attachment).ConfigureAwait(false);
            var replacement = path is null
                ? attachment.Fallback
                : $"[{attachment.Label} {path}]";
            line = line.Replace(attachment.Placeholder, replacement, StringComparison.Ordinal);
        }

        return line;
    }

    /// <summary>下载单个附件到缓存目录，成功返回本地绝对路径，失败或超限返回 null。</summary>
    private async Task<string?> DownloadAttachmentAsync(PendingAttachment attachment)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(AttachmentDownloadTimeout);

            using var response = await Http
                .GetAsync(attachment.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Context.Logger.LogWarning(
                    "下载{Label}失败：{Url} 返回 {Status}",
                    attachment.Label,
                    attachment.Url,
                    (int)response.StatusCode);
                return null;
            }

            if (response.Content.Headers.ContentLength is > MaxAttachmentBytes)
            {
                Context.Logger.LogWarning(
                    "跳过{Label}：{Url} 超过 {Limit} 字节",
                    attachment.Label,
                    attachment.Url,
                    MaxAttachmentBytes);
                return null;
            }

            var path = Path.Combine(
                _attachmentCacheDir,
                BuildAttachmentFileName(attachment, response.Content.Headers.ContentType?.MediaType));
            Directory.CreateDirectory(_attachmentCacheDir);

            var tooLarge = false;
            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (read == 0) break;

                    total += read;
                    if (total > MaxAttachmentBytes)
                    {
                        tooLarge = true;
                        break;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                }
            }

            if (tooLarge)
            {
                File.Delete(path);
                Context.Logger.LogWarning(
                    "跳过{Label}：{Url} 超过 {Limit} 字节",
                    attachment.Label,
                    attachment.Url,
                    MaxAttachmentBytes);
                return null;
            }

            Context.Logger.LogInformation("已保存{Label}到 {Path}", attachment.Label, path);
            return path;
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
        {
            Context.Logger.LogWarning("下载{Label}超时：{Url}", attachment.Label, attachment.Url);
            return null;
        }
        catch (Exception ex)
        {
            Context.Logger.LogWarning(ex, "下载{Label}失败：{Url}", attachment.Label, attachment.Url);
            return null;
        }
    }

    /// <summary>按地址哈希生成稳定的缓存文件名，尽量沿用原文件扩展名。</summary>
    private static string BuildAttachmentFileName(PendingAttachment attachment, string? contentType)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(attachment.Url)))[..12];

        var extension = Path.GetExtension(attachment.FileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(extension) && Uri.TryCreate(attachment.Url, UriKind.Absolute, out var uri))
            extension = Path.GetExtension(uri.AbsolutePath);

        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 10)
        {
            extension = contentType switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                _ => ".bin",
            };
        }

        return $"{attachment.Prefix}-{hash}{extension.ToLowerInvariant()}";
    }

    /// <summary>把一条消息写入所属会话的历史记录。</summary>
    private void RecordMessage(string conversationId, string line, string? displayName)
    {
        lock (_sync)
        {
            if (!_history.TryGetValue(conversationId, out var history))
            {
                history = [];
                _history[conversationId] = history;
            }

            history.Add(line);
            while (history.Count > MaxHistoryMessages) history.RemoveAt(0);

            if (!string.IsNullOrWhiteSpace(displayName)) _conversationNames[conversationId] = displayName;
            _lastActive[conversationId] = DateTimeOffset.Now;
            TrimConversations();
        }
    }

    /// <summary>会话数超过上限时，淘汰最久未活跃的会话及其历史与名称（调用方需持有锁）。</summary>
    private void TrimConversations()
    {
        while (_history.Count > MaxConversations)
        {
            string? oldest = null;
            var oldestTime = DateTimeOffset.MaxValue;

            foreach (var pair in _history)
            {
                var time = _lastActive.TryGetValue(pair.Key, out var lastActive)
                    ? lastActive
                    : DateTimeOffset.MinValue;
                if (time >= oldestTime) continue;

                oldest = pair.Key;
                oldestTime = time;
            }

            if (oldest is null) return;

            _history.Remove(oldest);
            _conversationNames.Remove(oldest);
            _lastActive.Remove(oldest);
        }
    }

    /// <summary>记住自己刚发出的一条消息 id，超出上限时淘汰最早的。</summary>
    private void RememberSentMessageId(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return;

        var id = messageId.Trim();
        lock (_sync)
        {
            if (!_sentMessageIdSet.Add(id)) return;

            _sentMessageIds.Enqueue(id);
            while (_sentMessageIds.Count > MaxSentMessageIds)
                _sentMessageIdSet.Remove(_sentMessageIds.Dequeue());
        }

        Context.Logger.LogDebug("已记录自己发出的消息 id：{Id}", id);
    }

    /// <summary>判断被引用的消息是否为桌宠自己发出的。</summary>
    private bool IsSentBySelf(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return false;

        var id = messageId.Trim();
        lock (_sync) return _sentMessageIdSet.Contains(id);
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
            var quoted = await FetchQuotedAsync(quotedId).ConfigureAwait(false);

            var label = quoted is { } value
                ? value.IsSelf
                    ? $"[引用你：{value.Summary}]"
                    : $"[引用消息({value.Sender}：{value.Summary})]"
                : IsSentBySelf(quotedId)
                    ? "[引用你的消息]"
                    : QuotePlaceholder;

            // 反查成功就把摘要缓存下来，之后读远端历史也能显示引用内容
            if (quoted is not null) RememberQuoteLabel(quotedId, label);

            // 解析阶段留的占位直接替换，避免留下两个引用标记
            var enriched = line.Contains(QuotePlaceholder, StringComparison.Ordinal)
                ? line.Replace(QuotePlaceholder, label, StringComparison.Ordinal)
                : PrefixQuote(line, label + " ");
            if (!string.Equals(enriched, line, StringComparison.Ordinal))
                PatchRecordedLine(conversationId, line, enriched);

            if (wake || quoted?.IsSelf == true) RaiseMentioned(enriched, sender, messageId);
        }
        catch (Exception ex)
        {
            Context.Logger.LogDebug(ex, "处理被引用消息 {QuotedId} 失败", quotedId);
            if (wake) RaiseMentioned(line, sender, messageId);
        }
    }

    /// <summary>用 get_msg 取回被引用消息的发送者与正文摘要；取不到时返回 null。</summary>
    private async Task<QuotedMessage?> FetchQuotedAsync(string quotedId)
    {
        ClientWebSocket? socket;
        long selfId;
        lock (_sync)
        {
            socket = _socket;
            selfId = _selfId;
        }

        if (socket is not { State: WebSocketState.Open }) return null;

        var response = await SendActionAsync(
                socket,
                "get_msg",
                new Dictionary<string, object?> { ["message_id"] = quotedId })
            .ConfigureAwait(false);

        if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;

        var quotedUserId = GetInt64(data, "user_id");
        if (quotedUserId <= 0 && data.TryGetProperty("sender", out var quotedSender))
            quotedUserId = GetInt64(quotedSender, "user_id");

        var summary = Summarize(ParseMessage(data).Text);

        Context.Logger.LogDebug(
            "引用消息 {QuotedId} 的发送者 {UserId}，桌宠自己 {SelfId}",
            quotedId,
            quotedUserId,
            selfId);

        return new QuotedMessage(
            quotedUserId > 0 && quotedUserId == selfId,
            GetSenderName(data, quotedUserId),
            summary);
    }

    /// <summary>在「[会话] 发送者：」之后插入引用标记。</summary>
    private static string PrefixQuote(string line, string prefix)
    {
        var index = line.IndexOf('：', StringComparison.Ordinal);
        return index >= 0 ? line[..(index + 1)] + prefix + line[(index + 1)..] : prefix + line;
    }

    /// <summary>把被引用的正文压成一行短摘要，避免长文把历史记录撑爆。</summary>
    private static string Summarize(string text)
    {
        var single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (single.Length == 0) return "[非文本消息]";

        return single.Length <= QuotedSummaryLength ? single : single[..QuotedSummaryLength] + "…";
    }

    /// <summary>把会话历史里的原始行换成补上引用内容的版本。</summary>
    private void PatchRecordedLine(string conversationId, string oldLine, string newLine)
    {
        lock (_sync)
        {
            if (!_history.TryGetValue(conversationId, out var history)) return;

            for (var i = history.Count - 1; i >= 0; i--)
            {
                if (string.Equals(history[i], oldLine, StringComparison.Ordinal))
                    history[i] = newLine;
            }
        }
    }

    /// <summary>记住已反查到的引用摘要，供后续渲染远端历史时直接使用。</summary>
    private void RememberQuoteLabel(string quotedId, string label)
    {
        lock (_sync)
        {
            if (_quoteLabels.ContainsKey(quotedId)) return;

            _quoteLabels[quotedId] = label;
            _quoteLabelOrder.Enqueue(quotedId);
            while (_quoteLabelOrder.Count > MaxQuoteLabels)
                _quoteLabels.Remove(_quoteLabelOrder.Dequeue());
        }
    }

    /// <summary>取缓存的引用摘要，未反查过时返回 null。</summary>
    private string? TryGetQuoteLabel(string quotedId)
    {
        lock (_sync) return _quoteLabels.GetValueOrDefault(quotedId);
    }

    /// <summary>被引用消息的摘要信息。</summary>
    private sealed record QuotedMessage(bool IsSelf, string Sender, string Summary);

    /// <summary>消息正文是否含有任一唤醒词（不区分大小写）；未配置唤醒词时始终返回 false。</summary>
    private bool ContainsWakeWord(string text)
    {
        foreach (var word in _wakeWords)
        {
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>概率回复（仅群聊生效）：非 @、非引用桌宠、也未命中唤醒词的消息，按配置的概率（0-1）随机命中一次唤醒；0 表示关闭。</summary>
    private bool RollReplyChance()
        => _replyProbability > 0 && Random.Shared.NextDouble() < _replyProbability;

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
            Context.Logger.LogWarning(ex, "向宿主转发 NapCat 平台提醒失败");
        }
    }

    private void CompletePending(string echo, JsonElement response)
    {
        TaskCompletionSource<JsonElement>? completion;
        lock (_sync)
        {
            if (!_pending.TryGetValue(echo, out completion)) return;
        }

        completion.TrySetResult(response.Clone());
    }

    /// <summary>发送一条 OneBot action，并等待带相同 echo 的响应；返回整个响应（含 data）。</summary>
    private async Task<JsonElement> SendActionAsync(
        ClientWebSocket socket,
        string action,
        Dictionary<string, object?> parameters)
    {
        var echo = Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["action"] = action,
            ["params"] = parameters,
            ["echo"] = echo,
        });

        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync) _pending[echo] = completion;

        // ClientWebSocket 不支持并发发送，所有 action（发送消息、查询历史、反查引用）统一排队
        await _sendGate.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token)
                .ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(ActionTimeout);
            var response = await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);

            var retcode = GetInt64(response, "retcode");
            if (retcode == 0) return response;

            var reason = GetString(response, "message") ?? GetString(response, "wording") ?? "未知错误";
            throw new InvalidOperationException($"NapCat 返回错误（retcode {retcode}）：{reason}");
        }
        finally
        {
            _sendGate.Release();
            lock (_sync) _pending.Remove(echo);
        }
    }

    /// <summary>按字数拆分超长回复，避免拆开代理对（emoji）。</summary>
    private static IEnumerable<string> SplitMessage(string text, int maxChars)
    {
        text = text.Trim();
        var index = 0;
        while (index < text.Length)
        {
            var length = Math.Min(maxChars, text.Length - index);
            if (index + length < text.Length && char.IsHighSurrogate(text[index + length - 1])) length--;
            if (length <= 0) break;

            yield return text.Substring(index, length);
            index += length;
        }
    }

    /// <summary>把会话 id 规范成 kind:id（小写 kind），无法识别时返回空串。</summary>
    private static string NormalizeConversationId(string conversationId)
        => TryParseConversationId(conversationId, out var conversation)
            ? $"{conversation.Kind}:{conversation.Id.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;

    /// <summary>解析会话 id：支持 group:群号 / private:QQ号，也兼容 群 / 私聊 前缀以及纯数字（按私聊处理）。</summary>
    private static bool TryParseConversationId(string conversationId, out (string Kind, long Id) conversation)
    {
        conversation = default;
        var raw = conversationId?.Trim() ?? string.Empty;
        if (raw.Length == 0) return false;

        var separator = raw.IndexOfAny([':', '：']);
        var kindText = separator < 0 ? "private" : raw[..separator].Trim();
        var numberText = separator < 0 ? raw : raw[(separator + 1)..].Trim();

        var kind = kindText.ToLowerInvariant() switch
        {
            "group" or "群" or "群聊" => "group",
            "private" or "user" or "私聊" or "用户" => "private",
            _ => string.Empty,
        };

        if (kind.Length == 0) return false;
        if (!long.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
            return false;

        conversation = (kind, id);
        return true;
    }

    private void ReadConfiguration()
    {
        var config = Context.Configuration;
        _wsUrl = GetConfigString(config, "wsUrl", "ws://127.0.0.1:3001");
        _accessToken = GetConfigString(config, "accessToken", string.Empty);
        _allowedUsers = ReadConfigIds(config, "allowedUsers");
        _allowedGroups = ReadConfigIds(config, "allowedGroups");
        _requireAtInGroup = GetConfigBool(config, "requireAtInGroup", true);
        _wakeOnPrivateMessage = GetConfigBool(config, "wakeOnPrivateMessage", true);
        _filterSelfMessage = GetConfigBool(config, "filterSelfMessage", false);
        _wakeWords = ReadConfigWords(config, "wakeWords");
        _replyProbability = Math.Clamp(GetConfigDouble(config, "replyProbability", 0), 0, 1);
        _maxReplyChars = Math.Clamp(GetConfigInt(config, "maxReplyChars", 1500), 200, 4000);
        _downloadAttachments = GetConfigBool(config, "downloadAttachments", true);
        _rolePrompt = GetConfigString(config, "rolePrompt", string.Empty).Trim();

        var cacheDir = GetConfigString(config, "attachmentCacheDir", "attachments").Trim();
        if (cacheDir.Length == 0) cacheDir = "attachments";
        _attachmentCacheDir = Path.GetFullPath(
            Path.IsPathRooted(cacheDir) ? cacheDir : Path.Combine(Context.PluginDirectory, cacheDir));
    }

    private static string GetConfigString(JsonElement? config, string key, string fallback)
    {
        if (config is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty(key, out var value))
            return fallback;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? fallback,
            JsonValueKind.Number => value.GetRawText(),
            _ => fallback,
        };
    }

    private static bool GetConfigBool(JsonElement? config, string key, bool fallback)
    {
        if (config is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty(key, out var value))
            return fallback;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => fallback,
        };
    }

    private static int GetConfigInt(JsonElement? config, string key, int fallback)
    {
        if (config is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty(key, out var value))
            return fallback;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => fallback,
        };
    }

    private static double GetConfigDouble(JsonElement? config, string key, double fallback)
    {
        if (config is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty(key, out var value))
            return fallback;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => fallback,
        };
    }

    /// <summary>读取 QQ 号/群号白名单：支持 config 里的数组，也兼容逗号或空白分隔的字符串。</summary>
    private static HashSet<long> ReadConfigIds(JsonElement? config, string key)
    {
        var ids = new HashSet<long>();
        if (config is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty(key, out var value))
            return ids;

        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    var raw = item.ValueKind == JsonValueKind.Number ? item.GetRawText() : item.GetString();
                    AddId(raw, ids);
                }

                break;

            case JsonValueKind.String:
                foreach (var part in (value.GetString() ?? string.Empty).Split(
                             [',', '，', ';', '；', ' ', '\n', '\r', '\t'],
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    AddId(part, ids);
                }

                break;

            default:
                AddId(value.GetRawText(), ids);
                break;
        }

        return ids;
    }

    private static void AddId(string? raw, HashSet<long> ids)
    {
        if (long.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
            ids.Add(id);
    }

    /// <summary>读取唤醒词列表：支持 config 里的数组，也兼容逗号或空白分隔的字符串；去重且忽略大小写。</summary>
    private static string[] ReadConfigWords(JsonElement? config, string key)
    {
        if (config is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty(key, out var value))
            return [];

        var words = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? raw)
        {
            var word = raw?.Trim();
            if (string.IsNullOrEmpty(word) || !seen.Add(word)) return;
            words.Add(word);
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    Add(item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText());
                break;

            case JsonValueKind.String:
                foreach (var part in (value.GetString() ?? string.Empty).Split(
                             [',', '，', ';', '；', ' ', '\n', '\r', '\t'],
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    Add(part);
                }

                break;

            default:
                Add(value.GetRawText());
                break;
        }

        return [.. words];
    }

    private static string GetSenderName(JsonElement root, long userId)
    {
        if (root.TryGetProperty("sender", out var sender) && sender.ValueKind == JsonValueKind.Object)
        {
            var card = GetString(sender, "card");
            if (!string.IsNullOrWhiteSpace(card)) return card;

            var nickname = GetString(sender, "nickname");
            if (!string.IsNullOrWhiteSpace(nickname)) return nickname;
        }

        return userId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>把文件段渲染成「[文件 名字]」，拿不到文件名时退回「[文件]」。</summary>
    private static string FormatFileSegment(string? fileName)
    {
        var name = fileName?.Trim();
        return string.IsNullOrEmpty(name) ? "[文件]" : $"[文件 {name}]";
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static long GetInt64(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => 0,
        };
    }

    private static string? FindCqArgument(string arguments, string key)
    {
        var prefix = key + "=";
        foreach (var part in arguments.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var item = part.Trim();
            if (item.StartsWith(prefix, StringComparison.Ordinal)) return item[prefix.Length..];
        }

        return null;
    }

    private static string UnescapeCqText(string text)
    {
        if (text.Length == 0) return text;

        return text
            .Replace("&#91;", "[", StringComparison.Ordinal)
            .Replace("&#93;", "]", StringComparison.Ordinal)
            .Replace("&#44;", ",", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);
    }
}
