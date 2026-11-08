// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using SoufflePet.Plugin.Sdk;

namespace SoufflePet.Plugins.NapCat;

/// <summary>会话历史：保存本次运行收到的消息，并在磁盘上持久化会话列表与最近消息。</summary>
internal sealed class NapCatConversationStore
{
    /// <summary>每个会话最多保留的历史消息条数，供按会话读取使用。</summary>
    public const int MaxHistoryMessages = 50;

    /// <summary>最多保留的会话数，超出时淘汰最久未活跃的会话。</summary>
    private const int MaxConversations = 200;

    /// <summary>会话缓存落盘间隔；只在有变更时写文件。</summary>
    private static readonly TimeSpan CacheSaveInterval = TimeSpan.FromSeconds(5);

    private const string ConversationCacheFileName = "conversations.json";

    private readonly ILogger _logger;
    private readonly string _dataDirectory;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, List<string>> _history = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>取指定会话最近 <paramref name="count" /> 条消息；没有记录时返回空列表。</summary>
    public IReadOnlyList<string> GetRecentLines(string conversationId, int count)
    {
        lock (_sync)
        {
            if (!_history.TryGetValue(conversationId, out var history) || history.Count == 0) return [];
            return [.. history.Skip(Math.Max(0, history.Count - count))];
        }
    }

    /// <summary>把一条消息写入所属会话的历史记录。</summary>
    public void RecordMessage(string conversationId, string line, string? displayName)
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

    /// <summary>把会话历史里的原始行换成补上引用内容的版本。</summary>
    public void PatchRecordedLine(string conversationId, string oldLine, string newLine)
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

    /// <summary>启动时恢复会话列表、显示名与最近消息，重启后仍能按会话读取。</summary>
    public void Load()
    {
        var path = ConversationCachePath;
        if (!File.Exists(path)) return;

        try
        {
            var cache = JsonSerializer.Deserialize<NapCatConversationCache>(File.ReadAllText(path));
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
            File.WriteAllText(temporary, JsonSerializer.Serialize(cache));
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
