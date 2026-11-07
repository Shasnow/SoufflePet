// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SoufflePet.Plugin.Sdk;

namespace SoufflePet.Plugins.QqBot;

/// <summary>
/// QQ 官方机器人消息平台：通过 WebSocket 网关接收群聊 @ 机器人与单聊消息，
/// 用 REST 接口按会话回复；优先使用被动回复（msg_id + msg_seq），窗口用尽时可按配置回退主动消息。
/// 会话 id 形如 group:&lt;group_openid&gt; 或 private:&lt;user_openid&gt;。
/// </summary>
public sealed class QqBotPlugin : PluginBase, IMessagePlatform
{
    /// <summary>每个会话最多保留的历史消息条数。</summary>
    private const int MaxHistoryMessages = 50;

    /// <summary>最多保留的会话数，超出时淘汰最久未活跃的会话。</summary>
    private const int MaxConversations = 200;

    /// <summary>单个入站附件允许下载的最大字节数。</summary>
    private const long MaxAttachmentBytes = 20L * 1024 * 1024;

    /// <summary>图片附件的发送上限。</summary>
    private const long MaxSendImageBytes = 5L * 1024 * 1024;

    /// <summary>其他文件的发送上限。</summary>
    private const long MaxSendFileBytes = 50L * 1024 * 1024;

    /// <summary>群聊被动回复窗口：5 分钟内最多 5 条。</summary>
    private static readonly TimeSpan PassiveGroupWindow = TimeSpan.FromMinutes(5);
    private const int PassiveGroupLimit = 5;

    /// <summary>单聊被动回复窗口：60 分钟内最多 4 条。</summary>
    private static readonly TimeSpan PassivePrivateWindow = TimeSpan.FromMinutes(60);
    private const int PassivePrivateLimit = 4;

    private static readonly TimeSpan AttachmentDownloadTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http = QqApiClient.CreateHttpClient();
    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Dictionary<string, List<string>> _history = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _conversationNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastActive = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PassiveReplyContext> _passive = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();

    /// <summary>会话缓存落盘间隔；只在有变更时写文件。</summary>
    private static readonly TimeSpan CacheSaveInterval = TimeSpan.FromSeconds(5);
    private const string ConversationCacheFileName = "conversations.json";

    private Timer? _cacheSaveTimer;
    private volatile bool _cacheDirty;

    private QqApiClient? _api;
    private QqGateway? _gateway;
    private Task? _pump;
    private bool _stopped;

    private string _appId = string.Empty;
    private string _clientSecret = string.Empty;
    private string _apiBaseUrl = string.Empty;
    private string _wsUrl = string.Empty;
    private HashSet<string> _allowedUsers = [];
    private HashSet<string> _allowedGroups = [];
    private Dictionary<string, string> _openIdNames = new(StringComparer.Ordinal);
    private bool _wakeOnPrivateMessage = true;
    private bool _allowActiveMessage = true;
    private bool _downloadAttachments = true;
    private int _maxReplyChars = 1500;
    private string _attachmentCacheDir = string.Empty;
    private string _rolePrompt = string.Empty;

    /// <summary>平台 id，供工具的 platform 参数使用。</summary>
    public string Id => "qqbot";

    /// <summary>平台角色说明，交给宿主注入系统提示词；未配置时返回 null（不注入）。</summary>
    public string? RolePrompt => _rolePrompt.Length == 0 ? null : _rolePrompt;

    /// <summary>收到需要桌宠处理的消息时触发，由宿主订阅后唤醒桌宠。</summary>
    public event EventHandler<PlatformIncomingMessage>? OnMentioned;

    protected override Task OnInitializedAsync(CancellationToken cancellationToken)
    {
        ReadConfiguration();
        LoadConversationCache();
        _cacheSaveTimer = new Timer(
            _ => SaveConversationCacheIfDirty(),
            null,
            CacheSaveInterval,
            CacheSaveInterval);

        if (_appId.Length == 0 || _clientSecret.Length == 0)
        {
            Context.Logger.LogWarning("QQ 机器人尚未配置 appId / clientSecret，插件保持注册但不会连接");
            return Task.CompletedTask;
        }

        if (!Uri.TryCreate(_apiBaseUrl, UriKind.Absolute, out var apiBase)
            || apiBase.Scheme is not ("http" or "https"))
        {
            Context.Logger.LogWarning("QQ 机器人 API 地址无效：{Url}，插件不会发起连接", _apiBaseUrl);
            return Task.CompletedTask;
        }

        if (!Uri.TryCreate(_wsUrl, UriKind.Absolute, out var wsUri)
            || wsUri.Scheme is not ("ws" or "wss"))
        {
            Context.Logger.LogWarning("QQ 机器人 WebSocket 地址无效：{Url}，插件不会发起连接", _wsUrl);
            return Task.CompletedTask;
        }

        var api = new QqApiClient(_appId, _clientSecret, _apiBaseUrl, Context.Logger);
        _api = api;

        Context.Logger.LogInformation(
            "QQ 机器人平台将连接 {Url}；单聊白名单 {Users} 个，群白名单 {Groups} 个",
            _wsUrl,
            _allowedUsers.Count,
            _allowedGroups.Count);

        var gateway = new QqGateway(wsUri, api.GetAccessTokenAsync, HandleDispatchAsync, Context.Logger);
        _gateway = gateway;
        _pump = Task.Run(() => gateway.RunAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>向指定会话发送消息；conversationId 形如 group:&lt;group_openid&gt; 或 private:&lt;user_openid&gt;。</summary>
    public async Task SendMessageAsync(string conversationId, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        var api = _api ?? throw new InvalidOperationException(
            "QQ 机器人尚未配置 appId / clientSecret，无法发送消息。");
        var target = ParseTarget(conversationId);

        await _sendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var chunks = SplitMessage(message.Trim(), _maxReplyChars).ToList();
            if (chunks.Count == 0) return;

            var slots = CountPassiveSlots(target);
            if (slots < chunks.Count && !_allowActiveMessage)
            {
                throw new InvalidOperationException(
                    $"被动回复窗口仅剩 {slots} 条，待发送 {chunks.Count} 条，且未开启主动消息回退。");
            }

            foreach (var chunk in chunks)
            {
                var passive = TryCreatePassiveReply(target);
                if (passive is null && !_allowActiveMessage)
                {
                    throw new InvalidOperationException(
                        "被动回复窗口已过期或条数用尽，且未开启主动消息回退。");
                }

                var sentId = await api
                    .SendTextAsync(target, chunk, passive, _cts.Token)
                    .ConfigureAwait(false);

                if (passive is not null) MarkPassiveSent(target, passive);

                Context.Logger.LogInformation(
                    "已向 {Conversation} 发送消息（{Mode}）{MessageId}",
                    ConversationKey(target),
                    passive is not null ? "被动回复" : "主动消息",
                    sentId ?? "-");
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>向指定会话发送本地图片或文件。</summary>
    public async Task SendAttachmentAsync(string conversationId, PlatformAttachment attachment)
    {
        var api = _api ?? throw new InvalidOperationException(
            "QQ 机器人尚未配置 appId / clientSecret，无法发送附件。");
        var target = ParseTarget(conversationId);

        var path = Path.GetFullPath(attachment.FilePath);
        if (!File.Exists(path))
            throw new InvalidOperationException($"文件不存在：{path}");

        var limit = attachment.Kind == PlatformAttachmentKind.Image
            ? MaxSendImageBytes
            : MaxSendFileBytes;
        if (new FileInfo(path).Length > limit)
        {
            throw new InvalidOperationException(
                $"附件超过 {limit / 1024 / 1024} MB 上限：{path}");
        }

        await _sendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // 上传期间窗口可能过期，这里只判断「当前是否还有被动回复资格」，
            // 真正的 passive 参数在发送前再创建，避免创建一个立即丢弃的对象。
            if (CountPassiveSlots(target) == 0 && !_allowActiveMessage)
            {
                throw new InvalidOperationException(
                    "被动回复窗口已过期或条数用尽，且未开启主动消息回退。");
            }

            var fileType = attachment.Kind == PlatformAttachmentKind.Image ? 1 : 4;
            var fileInfo = await api
                .UploadAsync(target, path, fileType, _cts.Token)
                .ConfigureAwait(false);

            var passive = TryCreatePassiveReply(target);
            if (passive is null && !_allowActiveMessage)
            {
                Context.Logger.LogWarning(
                    "QQ 附件已上传，但被动回复窗口在上传期间关闭且未开启主动消息，文件未发送");
                throw new InvalidOperationException(
                    "上传期间被动回复窗口已关闭，且未开启主动消息回退。");
            }

            await api
                .SendMediaAsync(target, fileInfo, passive, _cts.Token)
                .ConfigureAwait(false);

            if (passive is not null) MarkPassiveSent(target, passive);

            Context.Logger.LogInformation(
                "已向 {Conversation} 发送附件「{Name}」（{Mode}）",
                ConversationKey(target),
                attachment.DisplayName ?? Path.GetFileName(path),
                passive is not null ? "被动回复" : "主动消息");
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>列出本次运行期间收到过消息的会话，按最近活跃排序。</summary>
    public Task<IReadOnlyList<PlatformConversation>> GetConversationsAsync()
    {
        lock (_sync)
        {
            IReadOnlyList<PlatformConversation> list = _history
                .Where(pair => pair.Value.Count > 0)
                .OrderByDescending(pair =>
                    _lastActive.TryGetValue(pair.Key, out var time) ? time : DateTimeOffset.MinValue)
                .Select(pair => new PlatformConversation(
                    pair.Key,
                    _conversationNames.GetValueOrDefault(pair.Key)))
                .ToList();
            return Task.FromResult(list);
        }
    }

    /// <summary>读取该会话本次运行期间缓存的消息。</summary>
    public Task<string> GetMessagesAsync(string conversationId, int limit = 20)
    {
        var id = NormalizeConversationId(conversationId);
        if (id.Length == 0) return Task.FromResult(string.Empty);

        var count = Math.Clamp(limit, 1, MaxHistoryMessages);
        lock (_sync)
        {
            if (!_history.TryGetValue(id, out var history) || history.Count == 0)
                return Task.FromResult(string.Empty);

            return Task.FromResult(
                string.Join('\n', history.Skip(Math.Max(0, history.Count - count))));
        }
    }

    public override async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (_stopped) return;
        _stopped = true;

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放过，忽略
        }

        if (_gateway is not null)
            await _gateway.CloseAsync().ConfigureAwait(false);

        if (_pump is { } pump)
        {
            try
            {
                await pump.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Context.Logger.LogDebug("等待 QQ 网关退出超时");
            }
            catch (OperationCanceledException)
            {
                // 宿主正在退出，不阻塞
            }
            catch (Exception ex)
            {
                Context.Logger.LogDebug(ex, "等待 QQ 网关退出时出错");
            }
        }

        _cacheSaveTimer?.Dispose();
        _cacheSaveTimer = null;
        SaveConversationCacheIfDirty();

        _cts.Dispose();
        _http.Dispose();
    }

    private async Task HandleDispatchAsync(string eventName, JsonElement data)
    {
        try
        {
            switch (eventName)
            {
                case "C2C_MESSAGE_CREATE":
                    await HandleC2CAsync(data).ConfigureAwait(false);
                    break;

                case "GROUP_AT_MESSAGE_CREATE":
                    await HandleGroupAsync(data).ConfigureAwait(false);
                    break;

                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            Context.Logger.LogWarning(ex, "处理 QQ 事件 {Event} 失败", eventName);
        }
    }

    private async Task HandleC2CAsync(JsonElement data)
    {
        Context.Logger.LogDebug("收到 QQ 单聊事件：{data}", data);
        var openId = QqJson.GetString(data, "author", "user_openid");
        if (string.IsNullOrEmpty(openId)) return;
        if (_allowedUsers.Count > 0 && !_allowedUsers.Contains(openId)) return;

        var conversationId = $"private:{openId}";
        var messageId = RememberPassive(conversationId, QqJson.GetString(data, "id"));
        var content = QqJson.GetString(data, "content")?.Trim() ?? string.Empty;
        var sender = ResolveSender(openId, EventUsername(data));
        var line = await BuildLineAsync(conversationId, sender, content, data).ConfigureAwait(false);

        RecordMessage(conversationId, line, null);
        
        if (_wakeOnPrivateMessage) RaiseMentioned(line, sender, messageId);
    }

    private async Task HandleGroupAsync(JsonElement data)
    {
        Context.Logger.LogDebug("收到 QQ 群聊事件：{data}", data);
        var groupId = QqJson.GetString(data, "group_openid");
        if (string.IsNullOrEmpty(groupId)) return;
        if (_allowedGroups.Count > 0 && !_allowedGroups.Contains(groupId)) return;

        var sender = QqJson.GetString(data, "author", "member_openid");
        var senderLabel = sender is { Length: > 0 }
            ? ResolveSender(sender, EventUsername(data))
            : $"群#{ShortId(groupId)}";
        var conversationId = $"group:{groupId}";
        var messageId = RememberPassive(conversationId, QqJson.GetString(data, "id"));
        var content = QqJson.GetString(data, "content")?.Trim() ?? string.Empty;
        var line = await BuildLineAsync(conversationId, senderLabel, content, data).ConfigureAwait(false);

        RecordMessage(conversationId, line, null);

        RaiseMentioned(line, senderLabel, messageId);
    }

    private async Task<string> BuildLineAsync(
        string conversationId,
        string sender,
        string content,
        JsonElement data)
    {
        var line = $"[{conversationId}] {sender}：{content}";
        if (!_downloadAttachments) return line;

        if (!data.TryGetProperty("attachments", out var attachments)
            || attachments.ValueKind != JsonValueKind.Array)
        {
            return line;
        }

        var additions = new List<string>();
        foreach (var item in attachments.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var url = QqJson.GetString(item, "url");
            if (string.IsNullOrWhiteSpace(url)) continue;

            var contentType = QqJson.GetString(item, "content_type");
            var isImage = contentType is not null
                          && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
            var label = isImage ? "图片" : "文件";

            var path = await DownloadAttachmentAsync(
                    url,
                    QqJson.GetString(item, "filename"),
                    contentType)
                .ConfigureAwait(false);

            additions.Add(path is null ? $"[{label}]" : $"[{label} {path}]");
        }

        if (additions.Count > 0) line = line + "\n" + string.Join('\n', additions);
        return line;
    }

    /// <summary>发送者显示名：手动映射优先，其次事件里的 username，最后回退 openid。</summary>
    private string ResolveSender(string openId, string? username)
    {
        if (_openIdNames.TryGetValue(openId, out var mapped)) return mapped;
        return username ?? openId;
    }

    /// <summary>
    /// 事件里自带的用户昵称。文档的 User 结构定义了 username，但实际推送可能为空串，
    /// 空值时返回 null，由调用方回退到 openid。
    /// </summary>
    private static string? EventUsername(JsonElement data)
    {
        var name = QqJson.GetString(data, "author", "username")?.Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>openid 缩写：只保留前 8 个字符，仅用于缺少发送者时的群标识。</summary>
    private static string ShortId(string openId)
        => openId.Length <= 8 ? openId : openId[..8];

    private string? RememberPassive(string conversationId, string? messageId)
    {
        if (string.IsNullOrEmpty(messageId)) return null;

        lock (_sync)
        {
            _passive[conversationId] = new PassiveReplyContext(messageId);
        }

        return messageId;
    }

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
            Context.Logger.LogWarning(ex, "唤醒桌宠失败");
        }
    }

    private QqConversationTarget ParseTarget(string conversationId)
    {
        if (TryParseConversationId(conversationId, out var target)) return target;

        throw new ArgumentException(
            $"无法识别的会话 id「{conversationId}」，应形如 group:<group_openid> 或 private:<user_openid>。",
            nameof(conversationId));
    }

    private int CountPassiveSlots(QqConversationTarget target)
    {
        lock (_sync)
        {
            var context = _passive.GetValueOrDefault(ConversationKey(target));
            if (context is null) return 0;
            if (DateTimeOffset.Now - context.ReceivedAt > PassiveWindow(target.Kind)) return 0;

            return Math.Max(0, PassiveLimit(target.Kind) - context.ReplyCount);
        }
    }

    private QqPassiveReply? TryCreatePassiveReply(QqConversationTarget target)
    {
        lock (_sync)
        {
            var context = _passive.GetValueOrDefault(ConversationKey(target));
            if (context is null) return null;
            if (DateTimeOffset.Now - context.ReceivedAt > PassiveWindow(target.Kind)) return null;
            if (context.ReplyCount >= PassiveLimit(target.Kind)) return null;

            return new QqPassiveReply(context.MessageId, context.NextSeq);
        }
    }

    private void MarkPassiveSent(QqConversationTarget target, QqPassiveReply reply)
    {
        lock (_sync)
        {
            var context = _passive.GetValueOrDefault(ConversationKey(target));
            if (context is null || !string.Equals(context.MessageId, reply.MessageId, StringComparison.Ordinal))
                return;

            context.NextSeq = reply.Sequence + 1;
            context.ReplyCount++;
        }
    }

    private static TimeSpan PassiveWindow(QqConversationKind kind)
        => kind == QqConversationKind.Group ? PassiveGroupWindow : PassivePrivateWindow;

    private static int PassiveLimit(QqConversationKind kind)
        => kind == QqConversationKind.Group ? PassiveGroupLimit : PassivePrivateLimit;

    private static string ConversationKey(QqConversationTarget target)
        => target.Kind == QqConversationKind.Group
            ? $"group:{target.OpenId}"
            : $"private:{target.OpenId}";

    /// <summary>下载单个附件到缓存目录，成功返回本地绝对路径，失败或超限返回 null。</summary>
    private async Task<string?> DownloadAttachmentAsync(
        string url,
        string? fileName,
        string? contentType)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(AttachmentDownloadTimeout);

            using var response = await _http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Context.Logger.LogWarning(
                    "下载 QQ 附件失败：{Url} 返回 {Status}",
                    url,
                    (int)response.StatusCode);
                return null;
            }

            if (response.Content.Headers.ContentLength is > MaxAttachmentBytes)
            {
                Context.Logger.LogWarning("跳过 QQ 附件：{Url} 超过 {Limit} 字节", url, MaxAttachmentBytes);
                return null;
            }

            Directory.CreateDirectory(_attachmentCacheDir);
            var path = Path.Combine(
                _attachmentCacheDir,
                BuildAttachmentFileName(url, fileName, contentType));

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
                Context.Logger.LogWarning("跳过 QQ 附件：{Url} 超过 {Limit} 字节", url, MaxAttachmentBytes);
                return null;
            }

            Context.Logger.LogInformation("已保存 QQ 附件到 {Path}", path);
            return path;
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
        {
            Context.Logger.LogWarning("下载 QQ 附件超时：{Url}", url);
            return null;
        }
        catch (Exception ex)
        {
            Context.Logger.LogWarning(ex, "下载 QQ 附件失败：{Url}", url);
            return null;
        }
    }

    /// <summary>按地址哈希生成稳定的缓存文件名，尽量沿用原文件扩展名。</summary>
    private static string BuildAttachmentFileName(string url, string? fileName, string? contentType)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..12];

        var extension = Path.GetExtension(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(extension) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            extension = Path.GetExtension(uri.AbsolutePath);

        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 10)
        {
            extension = contentType?.ToLowerInvariant() switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                _ => ".bin",
            };
        }

        return $"qq-{hash}{extension.ToLowerInvariant()}";
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

        _cacheDirty = true;
    }

    /// <summary>会话数超过上限时，淘汰最久未活跃的会话（调用方需持有锁）。</summary>
    private void TrimConversations()
    {
        while (_history.Count > MaxConversations)
        {
            string? oldest = null;
            var oldestTime = DateTimeOffset.MaxValue;
            foreach (var key in _history.Keys)
            {
                var time = _lastActive.GetValueOrDefault(key, DateTimeOffset.MinValue);
                if (time >= oldestTime) continue;

                oldestTime = time;
                oldest = key;
            }

            if (oldest is null) break;

            _history.Remove(oldest);
            _conversationNames.Remove(oldest);
            _lastActive.Remove(oldest);
            _passive.Remove(oldest);
        }
    }

    /// <summary>会话缓存的落盘路径。</summary>
    private string ConversationCachePath
        => Path.Combine(Context.DataDirectory, ConversationCacheFileName);

    /// <summary>启动时恢复会话列表、显示名与最近消息，重启后仍能按会话读取。</summary>
    private void LoadConversationCache()
    {
        var path = ConversationCachePath;
        if (!File.Exists(path)) return;

        try
        {
            var cache = JsonSerializer.Deserialize<ConversationCache>(File.ReadAllText(path));
            if (cache is null) return;

            var restored = 0;
            lock (_sync)
            {
                foreach (var entry in cache.Conversations)
                {
                    if (string.IsNullOrWhiteSpace(entry.Id)) continue;

                    _history[entry.Id] = [.. (entry.Messages ?? [])];
                    if (!string.IsNullOrWhiteSpace(entry.Name)) _conversationNames[entry.Id] = entry.Name;
                    _lastActive[entry.Id] = entry.LastActive;
                    restored++;
                }

                TrimConversations();
            }

            Context.Logger.LogInformation("已恢复 {Count} 个 QQ 会话缓存", restored);
        }
        catch (Exception ex)
        {
            Context.Logger.LogWarning(ex, "读取 QQ 会话缓存失败，将使用空缓存");
        }
    }

    /// <summary>有变更时把会话缓存写入磁盘；由定时器与关闭流程调用。</summary>
    private void SaveConversationCacheIfDirty()
    {
        if (!_cacheDirty) return;
        _cacheDirty = false;

        ConversationCache cache;
        lock (_sync)
        {
            cache = new ConversationCache
            {
                Conversations =
                [
                    .. _history
                        .Where(pair => pair.Value.Count > 0)
                        .Select(pair => new ConversationCacheEntry
                        {
                            Id = pair.Key,
                            Name = _conversationNames.GetValueOrDefault(pair.Key),
                            LastActive = _lastActive.GetValueOrDefault(pair.Key, DateTimeOffset.MinValue),
                            Messages = [.. pair.Value],
                        }),
                ],
            };
        }

        try
        {
            Directory.CreateDirectory(Context.DataDirectory);
            var path = ConversationCachePath;
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(cache));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _cacheDirty = true;
            Context.Logger.LogWarning(ex, "写入 QQ 会话缓存失败");
        }
    }

    /// <summary>把会话 id 规范成 kind:id，无法识别时返回空串。</summary>
    private static string NormalizeConversationId(string conversationId)
        => TryParseConversationId(conversationId, out var target)
            ? ConversationKey(target)
            : string.Empty;

    /// <summary>解析会话 id：支持 group:&lt;group_openid&gt; / private:&lt;user_openid&gt;，也兼容 群/群聊、c2c/私聊 前缀。</summary>
    private static bool TryParseConversationId(
        string? conversationId,
        out QqConversationTarget target)
    {
        target = default;
        var raw = conversationId?.Trim() ?? string.Empty;
        if (raw.Length == 0) return false;

        var separator = raw.IndexOfAny([':', '：']);
        if (separator < 0) return false;

        var kindText = raw[..separator].Trim().ToLowerInvariant();
        var openId = raw[(separator + 1)..].Trim();
        if (openId.Length == 0) return false;

        QqConversationKind kind;
        switch (kindText)
        {
            case "group":
            case "群":
            case "群聊":
                kind = QqConversationKind.Group;
                break;

            case "private":
            case "user":
            case "c2c":
            case "私聊":
            case "用户":
                kind = QqConversationKind.Private;
                break;

            default:
                return false;
        }

        target = new QqConversationTarget(kind, openId);
        return true;
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

    private void ReadConfiguration()
    {
        var config = Context.Configuration;
        _appId = GetConfigString(config, "appId", string.Empty).Trim();
        _clientSecret = GetConfigString(config, "clientSecret", string.Empty).Trim();
        _apiBaseUrl = GetConfigString(config, "apiBaseUrl", "https://api.bot.qq.com").Trim();
        _wsUrl = GetConfigString(config, "wsUrl", "wss://api.bot.qq.com/websocket/").Trim();
        _allowedUsers = ReadConfigList(config, "allowedUsers");
        _allowedGroups = ReadConfigList(config, "allowedGroups");
        _openIdNames = ReadOpenIdNames(config);
        _wakeOnPrivateMessage = GetConfigBool(config, "wakeOnPrivateMessage", true);
        _allowActiveMessage = GetConfigBool(config, "allowActiveMessage", true);
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

    /// <summary>读取 openid → 昵称映射：每行一个 openid=昵称，忽略空行与 # 开头的注释。</summary>
    private static Dictionary<string, string> ReadOpenIdNames(JsonElement? config)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (config is not { ValueKind: JsonValueKind.Object } root
            || !root.TryGetProperty("openIdNames", out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return map;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;

            var text = item.GetString()?.Trim();
            if (string.IsNullOrEmpty(text) || text.StartsWith('#')) continue;

            var separator = text.IndexOf('=');
            if (separator <= 0) continue;

            var openId = text[..separator].Trim();
            var name = text[(separator + 1)..].Trim();
            if (openId.Length > 0 && name.Length > 0) map[openId] = name;
        }

        return map;
    }

    /// <summary>读取 openid 白名单：支持 config 里的数组，也兼容逗号或空白分隔的字符串。</summary>
    private static HashSet<string> ReadConfigList(JsonElement? config, string key)
    {
        var items = new HashSet<string>(StringComparer.Ordinal);
        if (config is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty(key, out var value))
            return items;

        void Add(string? raw)
        {
            var text = raw?.Trim();
            if (text is { Length: > 0 }) items.Add(text);
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    var raw = item.ValueKind == JsonValueKind.Number
                        ? item.GetRawText()
                        : item.GetString();
                    Add(raw);
                }

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

        return items;
    }

    /// <summary>一条入站消息建立的被动回复上下文。</summary>
    private sealed class PassiveReplyContext
    {
        public PassiveReplyContext(string messageId)
        {
            MessageId = messageId;
            ReceivedAt = DateTimeOffset.Now;
        }

        public string MessageId { get; }

        public DateTimeOffset ReceivedAt { get; }

        /// <summary>下一条被动回复的 msg_seq，同一 msg_id 下递增。</summary>
        public int NextSeq { get; set; } = 1;

        /// <summary>已发送的被动回复条数。</summary>
        public int ReplyCount { get; set; }
    }

    /// <summary>落盘的会话缓存。</summary>
    private sealed class ConversationCache
    {
        public List<ConversationCacheEntry> Conversations { get; set; } = [];
    }

    private sealed class ConversationCacheEntry
    {
        public string Id { get; set; } = string.Empty;

        public string? Name { get; set; }

        public DateTimeOffset LastActive { get; set; }

        public List<string> Messages { get; set; } = [];
    }
}
