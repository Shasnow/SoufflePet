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
/// 会话历史：忠实记录收到的消息（展示行、消息 id 与发送者），
/// 既供按会话读取历史，也供回复时反查 [quote:...]、[at:...]，并在磁盘上持久化。
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
            return [.. history.TakeLast(count).Select(message => message.Line)];
        }
    }

    /// <summary>取指定会话最近 <paramref name="count" /> 条消息（带消息 id），供唤醒上下文精确排除当前消息。</summary>
    public IReadOnlyList<NapCatStoredMessage> GetRecentMessages(string conversationId, int count)
    {
        lock (_sync)
        {
            if (!_history.TryGetValue(conversationId, out var history) || history.Count == 0) return [];
            return [.. history.TakeLast(count)];
        }
    }

    /// <summary>把一条收到的消息写入所属会话的历史记录。</summary>
    public void RecordMessage(
        string conversationId,
        string? messageId,
        string line,
        long userId,
        string sender,
        string? displayName)
        => Record(
            conversationId,
            new NapCatStoredMessage(
                messageId?.Trim() ?? string.Empty,
                line,
                userId,
                sender,
                DateTimeOffset.UtcNow),
            displayName);

    /// <summary>把一条自己发出的消息也记进历史：发送者用自身昵称（拿不到时退回 QQ 号），格式与收到的消息一致。</summary>
    public void RecordSentMessage(string conversationId, string? messageId, string text, string? senderName)
    {
        var body = text.Trim();
        if (body.Length == 0) return;

        var sender = string.IsNullOrWhiteSpace(senderName) ? "我" : senderName.Trim();
        Record(
            conversationId,
            new NapCatStoredMessage(
                // 保留消息 id：模型引用自己说过的话时，要靠它生成 reply 段
                messageId?.Trim() ?? string.Empty,
                $"[{conversationId}] {sender}：{body}",
                0,
                sender,
                DateTimeOffset.UtcNow),
            displayName: null);
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
    /// 把已记录消息的展示行换成补全后的版本（补上附件路径、合并转发或引用内容）。
    /// 优先按消息 id 定位；没有消息 id 时退回按旧展示行匹配。
    /// </summary>
    public void PatchMessage(string conversationId, string? messageId, string oldLine, string newLine)
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
                    : string.Equals(item.Line, oldLine, StringComparison.Ordinal);
                if (!matched) continue;

                history[index] = item with { Line = newLine };
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
                // 只比对正文：展示行还带着「[会话] 发送者：」前缀，用整行匹配会误命中
                if (!NormalizeForMatch(StripPrefix(key, item.Line)).Contains(quote, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (found is not null) return null; // 多条命中：无法判断引用哪条
                found = item.MessageId;
            }

            return found;
        }
    }

    /// <summary>
    /// 按昵称反查 QQ 号：先精确匹配、再按包含匹配；只有唯一命中才返回，
    /// 多个相似昵称时返回 0，避免 @ 到错误的人。
    /// </summary>
    public long FindUserIdBySender(string conversationId, string senderName)
    {
        var name = senderName.Trim();
        if (name.Length == 0) return 0;

        lock (_sync)
        {
            if (!_history.TryGetValue(conversationId.Trim(), out var history)) return 0;

            var exact = FindUserId(history, item => string.Equals(item.Sender, name, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact.Value;

            var fuzzy = FindUserId(history, item => item.Sender.Contains(name, StringComparison.OrdinalIgnoreCase));
            return fuzzy ?? 0;
        }
    }

    /// <summary>在历史里找唯一满足条件的发送者 QQ 号；没有命中或多条命中都返回 null。</summary>
    private static long? FindUserId(List<NapCatStoredMessage> history, Func<NapCatStoredMessage, bool> predicate)
    {
        long? found = null;
        for (var index = history.Count - 1; index >= 0; index--)
        {
            var item = history[index];
            if (item.UserId <= 0 || !predicate(item)) continue;

            if (found is not null) return null;
            found = item.UserId;
        }

        return found;
    }

    /// <summary>从展示行里去掉「[会话 id] 发送者：」前缀，只留正文。</summary>
    private static string StripPrefix(string conversationId, string line)
    {
        var prefix = $"[{conversationId}] ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return line;

        var index = line.IndexOf('：', prefix.Length);
        return index >= 0 ? line[(index + 1)..] : line[prefix.Length..];
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

                    _history[entry.Id] = [.. (entry.Messages ?? [])];
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
