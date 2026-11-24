// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using SoufflePet.Plugin.Sdk;

namespace SoufflePet.Plugins.NapCat;

/// <summary>
/// 会话历史：忠实记录收到的消息（正文、消息 id、发送者，以及「@ 了你 / 引用了谁」这些<strong>结构化字段</strong>），
/// 既供按会话读取历史，也供回复时反查 [quote:...]、[at:...]，并在磁盘上持久化。
/// 记录只存正文、发送者与标记字段，「发送者（标记）：正文」这类展示行在读取时由
/// <see cref="NapCatStoredMessage.FormatLine" /> 拼出——拼与剥只存在一处，不会再漂移。
/// 行内不再重复会话 id：调用方本来就知道自己在读哪个会话。
/// </summary>
internal sealed class NapCatConversationStore
{
    /// <summary>每个会话最多保留的历史消息条数；同时决定引用与 @ 的反查窗口。</summary>
    public const int MaxHistoryMessages = 200;

    /// <summary>最多保留的会话数，超出时淘汰最久未活跃的会话。</summary>
    private const int MaxConversations = 200;

    /// <summary>引用片段的最小长度（去空白后）：太短的片段无法唯一定位到某条消息。</summary>
    private const int MinQuoteMatchLength = 2;

    /// <summary>会话缓存落盘间隔；只在有变更时写文件。</summary>
    private static readonly TimeSpan CacheSaveInterval = TimeSpan.FromSeconds(5);

    /// <summary>缓存序列化选项：中文不转义成 \uXXXX，避免文件体积膨胀数倍。</summary>
    private static readonly JsonSerializerOptions CacheJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const string ConversationCacheFileName = "conversations.json";

    private readonly ILogger _logger;
    private readonly string _dataDirectory;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, List<NapCatStoredMessage>> _history = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _conversationNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastActive = new(StringComparer.OrdinalIgnoreCase);

    private Timer? _cacheSaveTimer;
    private volatile bool _cacheDirty;

    public NapCatConversationStore(ILogger logger, string dataDirectory)
    {
        _logger = logger;
        _dataDirectory = dataDirectory;
    }

    /// <summary>列出本次运行期间收到过消息的会话，按最近活跃排序。</summary>
    public IReadOnlyList<PlatformConversation> GetConversations()
    {
        lock (_sync)
        {
            return
            [
                .. _history
                    .Where(pair => pair.Value.Count > 0)
                    .OrderByDescending(pair =>
                        _lastActive.TryGetValue(pair.Key, out var time) ? time : DateTimeOffset.MinValue)
                    .Select(pair => new PlatformConversation(pair.Key, _conversationNames.GetValueOrDefault(pair.Key)))
            ];
        }
    }

    /// <summary>取指定会话最近 <paramref name="count" /> 条消息的展示行；没有记录时返回空列表。</summary>
    public IReadOnlyList<string> GetRecentLines(string conversationId, int count)
    {
        lock (_sync)
        {
            if (!_history.TryGetValue(conversationId, out var history) || history.Count == 0) return [];
            return [.. history.TakeLast(count).Select(message => message.FormatLine())];
        }
    }

    /// <summary>
    /// 取桌宠上次回复之后、且在 <paramref name="currentMessageId" /> 之前的消息
    /// （按时间正序，最多保留最近 <paramref name="maxCount" /> 条），用作唤醒时附带的上下文；
    /// 还没回复过时退回最近的消息。自己的消息按发起人 QQ 号（<paramref name="selfId" />）识别。
    /// 以当前消息为截止点是必要的：异步补全期间可能已有更新的消息入队，
    /// 那些消息不属于当前这条的上下文，也不能把「比当前消息更晚的回复」当成上次回复。
    /// </summary>
    public IReadOnlyList<NapCatStoredMessage> GetMessagesSinceLastReply(
        string conversationId,
        int maxCount,
        long selfId,
        string? currentMessageId = null)
    {
        if (maxCount <= 0) return [];

        lock (_sync)
        {
            if (!_history.TryGetValue(conversationId, out var history) || history.Count == 0) return [];

            // 当前消息的位置（不含它本身）；找不到（已被淘汰）时取到历史末尾
            var end = history.Count;
            if (currentMessageId is { Length: > 0 })
            {
                for (var index = history.Count - 1; index >= 0; index--)
                {
                    if (!string.Equals(history[index].MessageId, currentMessageId, StringComparison.Ordinal))
                        continue;
                    end = index;
                    break;
                }
            }

            // 在范围内从后往前找自己最后一条回复，它之后的才是「这次要回的内容」
            var start = 0;
            if (selfId > 0)
            {
                for (var index = end - 1; index >= 0; index--)
                {
                    if (history[index].UserId != selfId) continue;
                    start = index + 1;
                    break;
                }
            }

            if (start >= end) return [];
            return [.. history.Skip(start).Take(end - start).TakeLast(maxCount)];
        }
    }

    /// <summary>把一条收到的消息写入所属会话的历史记录；<paramref name="text" /> 是纯正文，装饰走字段。</summary>
    public void RecordMessage(
        string conversationId,
        string? messageId,
        string text,
        long userId,
        string sender,
        string? displayName,
        bool mentionsSelf = false,
        string? quotedId = null)
        => Record(
            conversationId,
            new NapCatStoredMessage(
                messageId?.Trim() ?? string.Empty,
                text,
                userId,
                sender,
                DateTimeOffset.UtcNow,
                mentionsSelf,
                quotedId),
            displayName);

    /// <summary>
    /// 把一条自己发出的消息也记进历史：发送者用自身昵称（拿不到时退回 QQ 号），
    /// 发起人 QQ 号用自己的 id，读取时据此区分「谁在说」。
    /// </summary>
    public void RecordSentMessage(
        string conversationId,
        string? messageId,
        string text,
        string? senderName,
        long selfId)
    {
        var body = text.Trim();
        if (body.Length == 0) return;

        var sender = string.IsNullOrWhiteSpace(senderName) ? "我" : senderName.Trim();
        Record(
            conversationId,
            new NapCatStoredMessage(
                // 保留消息 id：模型引用自己说过的话时，要靠它生成 reply 段
                messageId?.Trim() ?? string.Empty,
                body,
                selfId,
                sender,
                DateTimeOffset.UtcNow),
            displayName: null);
    }

    /// <summary>
    /// 把远端拉到的历史合并进来：按消息 id 去重（已有记录保持原样，避免覆盖已经补全的正文），
    /// 再按时间排序并裁剪到上限。
    /// </summary>
    public void MergeHistory(string conversationId, IReadOnlyList<NapCatStoredMessage> messages)
    {
        if (messages.Count == 0) return;

        lock (_sync)
        {
            if (!_history.TryGetValue(conversationId, out var history))
            {
                history = [];
                _history[conversationId] = history;
            }

            var known = history
                .Where(item => item.MessageId.Length > 0)
                .Select(item => item.MessageId)
                .ToHashSet(StringComparer.Ordinal);

            var added = 0;
            foreach (var message in messages)
            {
                if (message.MessageId.Length > 0 && !known.Add(message.MessageId)) continue;

                history.Add(message);
                added++;
            }

            if (added == 0) return;

            // 合并后按时间重排：断线期间的消息插到原有记录之间也不会乱序
            history.Sort((left, right) => left.ReceivedAt.CompareTo(right.ReceivedAt));
            while (history.Count > MaxHistoryMessages) history.RemoveAt(0);

            _lastActive[conversationId] = DateTimeOffset.Now;
            TrimConversations();
        }

        _cacheDirty = true;
    }

    private void Record(string conversationId, NapCatStoredMessage message, string? displayName)
    {
        lock (_sync)
        {
            if (!_history.TryGetValue(conversationId, out var history))
            {
                history = [];
                _history[conversationId] = history;
            }

            history.Add(message);
            while (history.Count > MaxHistoryMessages) history.RemoveAt(0);

            if (!string.IsNullOrWhiteSpace(displayName)) _conversationNames[conversationId] = displayName;
            _lastActive[conversationId] = DateTimeOffset.Now;
            TrimConversations();
        }

        _cacheDirty = true;
    }

    /// <summary>
    /// 把已记录消息的正文换成补全后的版本（补上附件路径或合并转发内容）。
    /// 优先按消息 id 定位；没有消息 id 时退回按旧正文匹配。
    /// </summary>
    public void PatchMessage(string conversationId, string? messageId, string oldText, string newText)
    {
        var id = messageId?.Trim();

        lock (_sync)
        {
            if (!_history.TryGetValue(conversationId, out var history)) return;

            for (var index = history.Count - 1; index >= 0; index--)
            {
                var item = history[index];
                var matched = id is { Length: > 0 }
                    ? string.Equals(item.MessageId, id, StringComparison.Ordinal)
                    : string.Equals(item.Text, oldText, StringComparison.Ordinal);
                if (!matched) continue;

                history[index] = item with { Text = newText };
                break;
            }
        }

        _cacheDirty = true;
    }

    /// <summary>
    /// 反查到被引用消息后，把摘要（「发送者：正文」）写回这条记录。
    /// 摘要与正文分开存：读历史时才拼成「（引用 …）」，正文本身始终是对方写的内容。
    /// 只按消息 id 定位：平台没给消息 id 时历史里不会记下摘要（上报给宿主的 <c>Quote</c> 不受影响）。
    /// </summary>
    public void PatchQuote(string conversationId, string? messageId, string quote)
    {
        var id = messageId?.Trim();
        if (id is not { Length: > 0 } || string.IsNullOrWhiteSpace(quote)) return;

        lock (_sync)
        {
            if (!_history.TryGetValue(conversationId, out var history)) return;

            for (var index = history.Count - 1; index >= 0; index--)
            {
                var item = history[index];
                if (!string.Equals(item.MessageId, id, StringComparison.Ordinal)) continue;

                history[index] = item with { Quote = quote.Trim() };
                break;
            }
        }

        _cacheDirty = true;
    }

    /// <summary>
    /// 按引用内容反查消息 id：从最近收到的消息往前找，忽略大小写与空白差异。
    /// 片段太短或命中多条时宁可不引用，避免把引用挂到错误的消息上。
    /// </summary>
    public string? FindMessageIdByText(string conversationId, string quotedText)
    {
        var quote = NormalizeForMatch(quotedText);
        if (quote.Length < MinQuoteMatchLength) return null;

        var key = conversationId.Trim();
        lock (_sync)
        {
            if (!_history.TryGetValue(key, out var history)) return null;

            string? found = null;
            for (var index = history.Count - 1; index >= 0; index--)
            {
                var item = history[index];
                if (item.MessageId.Length == 0) continue;
                // 只比对正文
                if (!NormalizeForMatch(item.Text).Contains(quote, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (found is not null) return null; // 多条命中：无法判断引用哪条
                found = item.MessageId;
            }

            return found;
        }
    }

    /// <summary>
    /// 解析回复里 [quote:…] 的目标：先当作消息 id 在历史里找，找不到再按内容片段反查。
    /// </summary>
    public string? ResolveQuoteTarget(string conversationId, string target)
    {
        var key = conversationId.Trim();
        var value = target.Trim();
        if (key.Length == 0 || value.Length == 0) return null;

        lock (_sync)
        {
            if (!_history.TryGetValue(key, out var history)) return null;

            for (var index = history.Count - 1; index >= 0; index--)
            {
                var item = history[index];
                if (item.MessageId.Length > 0
                    && string.Equals(item.MessageId, value, StringComparison.Ordinal))
                    return item.MessageId;
            }
        }

        return FindMessageIdByText(conversationId, value);
    }

    /// <summary>
    /// 按昵称反查 QQ 号：先精确匹配、再按包含匹配；只有唯一命中才返回，
    /// 多个相似昵称时返回 0，避免 @ 到错误的人。
    /// <paramref name="excludeUserId" /> 用来排除桌宠自己：自己的消息也带 QQ 号，
    /// 否则模型写 <c>[at:自己的昵称]</c> 时会解析成机器人自己。
    /// </summary>
    public long FindUserIdBySender(string conversationId, string senderName, long excludeUserId = 0)
    {
        var name = senderName.Trim();
        if (name.Length == 0) return 0;

        lock (_sync)
        {
            if (!_history.TryGetValue(conversationId.Trim(), out var history)) return 0;

            var exact = FindUserId(
                history,
                item => string.Equals(item.Sender, name, StringComparison.OrdinalIgnoreCase),
                excludeUserId);
            if (exact is not null) return exact.Value;

            var fuzzy = FindUserId(
                history,
                item => item.Sender.Contains(name, StringComparison.OrdinalIgnoreCase),
                excludeUserId);
            return fuzzy ?? 0;
        }
    }

    /// <summary>
    /// 在历史里找唯一满足条件的发送者 QQ 号：同一人多条消息只算一次命中，
    /// 出现两个不同用户时返回 null，避免 @ 到错误的人。
    /// </summary>
    private static long? FindUserId(
        List<NapCatStoredMessage> history,
        Func<NapCatStoredMessage, bool> predicate,
        long excludeUserId = 0)
    {
        long? found = null;
        for (var index = history.Count - 1; index >= 0; index--)
        {
            var item = history[index];
            if (item.UserId <= 0 || item.UserId == excludeUserId || !predicate(item)) continue;

            if (found is null)
            {
                found = item.UserId;
                continue;
            }

            // 不同用户都命中：昵称区分不出来，宁可退回纯文本
            if (found != item.UserId) return null;
        }

        return found;
    }

    /// <summary>匹配引用内容与昵称时去掉所有空白，避免换行、空格差异导致匹配失败。</summary>
    private static string NormalizeForMatch(string text)
        => new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>启动时恢复会话列表、显示名与最近消息，重启后仍能按会话读取。</summary>
    public void Load()
    {
        var path = ConversationCachePath;
        if (!File.Exists(path)) return;

        try
        {
            var cache = JsonSerializer.Deserialize<NapCatConversationCache>(File.ReadAllText(path), CacheJson);
            if (cache is null) return;

            var restored = 0;
            lock (_sync)
            {
                foreach (var entry in cache.Conversations)
                {
                    if (string.IsNullOrWhiteSpace(entry.Id)) continue;

                    // 旧缓存存的是拼好前缀的展示行（没有正文），这类记录读出来是空正文：直接丢弃，
                    // 避免把空行喂给模型；下一次落盘时它们就不会再出现。
                    // 只有 @ / 引用、没有正文的消息保留（正文为空但装饰字段有值）。
                    var messages = (entry.Messages ?? [])
                        .Where(message => !string.IsNullOrWhiteSpace(message.Text)
                                          || message.MentionsSelf
                                          || message.QuotedId is not null)
                        .ToList();
                    if (messages.Count == 0) continue;

                    _history[entry.Id] = messages;
                    if (!string.IsNullOrWhiteSpace(entry.Name)) _conversationNames[entry.Id] = entry.Name;
                    _lastActive[entry.Id] = entry.LastActive;
                    restored++;
                }

                TrimConversations();
            }

            _logger.LogInformation("已恢复 {Count} 个 NapCat 会话缓存", restored);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 NapCat 会话缓存失败，将使用空缓存");
        }
    }

    /// <summary>启动定时落盘；只在有变更时写文件。</summary>
    public void StartAutoSave()
    {
        _cacheSaveTimer = new Timer(
            _ => SaveIfDirty(),
            null,
            CacheSaveInterval,
            CacheSaveInterval);
    }

    /// <summary>停止定时落盘，并做一次最终保存。</summary>
    public void StopAutoSave()
    {
        _cacheSaveTimer?.Dispose();
        _cacheSaveTimer = null;
        SaveIfDirty();
    }

    /// <summary>有变更时把会话缓存写入磁盘；由定时器与关闭流程调用。</summary>
    public void SaveIfDirty()
    {
        if (!_cacheDirty) return;
        _cacheDirty = false;

        NapCatConversationCache cache;
        lock (_sync)
        {
            cache = new NapCatConversationCache
            {
                Conversations =
                [
                    .. _history
                        .Where(pair => pair.Value.Count > 0)
                        .Select(pair => new NapCatConversationCacheEntry
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
            Directory.CreateDirectory(_dataDirectory);
            var cachePath = ConversationCachePath;
            var temporary = cachePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(cache, CacheJson));
            File.Move(temporary, cachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _cacheDirty = true;
            _logger.LogWarning(ex, "写入 NapCat 会话缓存失败");
        }
    }

    /// <summary>会话缓存的落盘路径。</summary>
    private string ConversationCachePath => Path.Combine(_dataDirectory, ConversationCacheFileName);

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
}
