// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Serilog;
using SoufflePet.Models;

namespace SoufflePet.Services;

/// <summary>恢复界面用的一轮历史对话。</summary>
public readonly record struct MemoryTurn(DateTimeOffset Time, string Role, string Text);

/// <summary>
/// 长期记忆：对话落盘、用户档案与关键词检索。
/// 数据保存在 %AppData%\SoufflePet 下（不随安装目录变化）：
/// conversations.jsonl 记录每轮问答与工具调用，profile.json 保存模型用 remember 写入的事实。
/// 检索使用中文 2-gram + BM25，不依赖分词库与向量服务。
/// </summary>
public sealed class ChatMemory
{
    /// <summary>用户角色。</summary>
    public const string UserRole = "user";

    /// <summary>助手角色。</summary>
    public const string AssistantRole = "assistant";

    /// <summary>工具调用记录（只在长期记忆里保留，不参与界面恢复与请求历史）。</summary>
    public const string ToolRole = "tool";

    /// <summary>内存里保留的最大历史轮数，也是启动时读取的行数。</summary>
    private const int MaxTurnsInMemory = 2000;

    /// <summary>档案条数上限，超出丢弃最久未更新的。</summary>
    private const int MaxProfileEntries = 30;

    /// <summary>注入系统提示词的记忆块长度上限（字符）。</summary>
    private const int MaxMemoryBlockChars = 1200;

    /// <summary>档案部分的长度上限（字符）。</summary>
    private const int MaxProfileChars = 480;

    /// <summary>单条回忆的展示长度上限（字符）。</summary>
    private const int MaxSnippetChars = 160;

    /// <summary>一次检索最多注入几条回忆。</summary>
    private const int SearchTopK = 3;

    /// <summary>对话文件超过该大小时重写成最近若干轮，避免无限增长。</summary>
    private const long MaxConversationFileBytes = 5 * 1024 * 1024;

    private const double Bm25K1 = 1.2;
    private const double Bm25B = 0.75;

    private static readonly ILogger Logger = Log.ForContext<ChatMemory>();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions LineJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly Lock _gate = new();
    private readonly SouffleSettings _settings;
    private readonly string _conversationsPath;
    private readonly string _profilePath;
    private readonly List<Turn> _turns = [];
    private List<MemoryEntry> _profile = [];

    public ChatMemory(SouffleSettings settings)
    {
        _settings = settings;

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SoufflePet");
        Directory.CreateDirectory(root);
        _conversationsPath = Path.Combine(root, "conversations.jsonl");
        _profilePath = Path.Combine(root, "profile.json");

        LoadProfile();
        LoadTurns();
    }

    /// <summary>记忆总开关（设置里可关掉注入，已落盘的内容保留）。</summary>
    public bool Enabled => _settings.ChatMemoryEnabled;

    /// <summary>追加一条记录并落盘；role 为 user / assistant / tool。</summary>
    public void AddTurn(string role, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;

        var turn = new Turn { Time = DateTimeOffset.Now, Role = role, Text = text };
        lock (_gate)
        {
            _turns.Add(turn);
            if (_turns.Count > MaxTurnsInMemory)
                _turns.RemoveRange(0, _turns.Count - MaxTurnsInMemory);

            try
            {
                File.AppendAllText(_conversationsPath,
                    JsonSerializer.Serialize(turn, LineJsonOptions) + Environment.NewLine);
                if (_turns.Count % 50 == 0) TrimConversationFile();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "append conversation failed");
            }
        }
    }

    /// <summary>
    /// 最近若干轮对话（用于界面恢复）；会跳过工具调用记录，并裁掉开头的助手消息，
    /// 避免从半截对话开始。
    /// </summary>
    public IReadOnlyList<MemoryTurn> RecentTurns(int maxTurns)
    {
        lock (_gate)
        {
            var skip = Math.Max(0, _turns.Count - maxTurns);
            var turns = _turns.Skip(skip)
                .Where(t => !IsToolRole(t.Role))
                .Select(t => new MemoryTurn(t.Time, t.Role, t.Text))
                .ToList();
            while (turns.Count > 0 && !IsUserRole(turns[0].Role)) turns.RemoveAt(0);
            return turns;
        }
    }

    /// <summary>写入/覆盖一条档案；value 为空等同于删除。</summary>
    public string Remember(string key, string value)
    {
        if (!Enabled) return "长期记忆已在设置里关闭，这条没有保存。";

        key = key.Trim();
        value = value.Trim();
        if (key.Length == 0) return "记忆的键不能为空。";
        if (key.Length > 30) key = key[..30];
        if (value.Length == 0) return Forget(key);
        if (value.Length > 200) value = value[..200];

        lock (_gate)
        {
            var existing = _profile.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Value = value;
                existing.UpdatedAt = DateTimeOffset.Now;
            }
            else
            {
                _profile.Add(new MemoryEntry { Key = key, Value = value, UpdatedAt = DateTimeOffset.Now });
                if (_profile.Count > MaxProfileEntries)
                {
                    _profile = _profile
                        .OrderByDescending(e => e.UpdatedAt)
                        .Take(MaxProfileEntries)
                        .ToList();
                }
            }

            SaveProfile();
        }

        return $"已记住：{key} = {value}";
    }

    /// <summary>删除一条档案；不存在时如实返回。</summary>
    public string Forget(string key)
    {
        if (!Enabled) return "长期记忆已在设置里关闭。";

        key = key.Trim();
        if (key.Length == 0) return "记忆的键不能为空。";

        lock (_gate)
        {
            var removed = _profile.RemoveAll(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return $"没有找到记忆：{key}";
            SaveProfile();
        }

        return $"已删除记忆：{key}";
    }

    /// <summary>按关键词检索历史对话，返回给模型看的文本。</summary>
    public string Recall(string query)
    {
        if (!Enabled) return "长期记忆已在设置里关闭。";

        var results = Search(query);
        if (results.Count == 0) return $"没有找到与「{query}」相关的历史对话。";

        var builder = new StringBuilder();
        foreach (var turn in results)
        {
            builder.Append("- [")
                .Append(turn.Time.ToString("yyyy-MM-dd HH:mm"))
                .Append("] ")
                .Append(RoleLabel(turn.Role))
                .Append('：')
                .AppendLine(Truncate(turn.Text, MaxSnippetChars * 2));
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// 生成本轮要注入系统提示词的记忆块：档案全量（限长）+ 与 query 相关的回忆。
    /// 关闭记忆或没有任何内容时返回空串。
    /// </summary>
    public string BuildMemoryBlock(string? query)
    {
        if (!Enabled) return string.Empty;

        List<string> profileLines;
        lock (_gate)
        {
            profileLines =
            [
                .. _profile
                    .OrderByDescending(e => e.UpdatedAt)
                    .Select(e => $"- {e.Key}：{e.Value}")
            ];
        }

        var snippets = string.IsNullOrWhiteSpace(query) ? [] : Search(query);

        var profileBuilder = new StringBuilder();
        foreach (var line in profileLines)
        {
            if (profileBuilder.Length + line.Length + 1 > MaxProfileChars) break;
            profileBuilder.AppendLine(line);
        }

        if (profileBuilder.Length == 0 && snippets.Count == 0) return string.Empty;

        var builder = new StringBuilder();
        builder.AppendLine("【长期记忆】以下是之前对话里保存下来的信息，回答时可以自然使用，不要生硬复述。");
        if (profileBuilder.Length > 0)
        {
            builder.AppendLine("用户档案：");
            builder.Append(profileBuilder);
        }

        if (snippets.Count > 0)
        {
            builder.AppendLine("相关回忆：");
            foreach (var turn in snippets)
            {
                builder.Append("- [")
                    .Append(turn.Time.ToString("yyyy-MM-dd HH:mm"))
                    .Append("] ")
                    .Append(RoleLabel(turn.Role))
                    .Append('：')
                    .AppendLine(Truncate(turn.Text, MaxSnippetChars));
            }
        }

        var block = builder.ToString().TrimEnd();
        return block.Length <= MaxMemoryBlockChars ? block : block[..MaxMemoryBlockChars];
    }

    private List<MemoryTurn> Search(string? query)
    {
        var results = new List<MemoryTurn>();
        if (!Enabled || string.IsNullOrWhiteSpace(query)) return results;

        var queryTokens = Tokenize(query).Distinct(StringComparer.Ordinal).ToArray();
        if (queryTokens.Length == 0) return results;

        lock (_gate)
        {
            var count = _turns.Count;
            if (count == 0) return results;

            var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var turn in _turns)
            {
                turn.EnsureTokens();
                foreach (var term in turn.Tokens.Distinct(StringComparer.Ordinal))
                    documentFrequency[term] = documentFrequency.GetValueOrDefault(term) + 1;
            }

            var averageLength = _turns.Average(t => t.Tokens.Length);
            if (averageLength <= 0) return results;

            var scored = new List<(Turn Turn, double Score)>(count);
            foreach (var turn in _turns)
            {
                var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var term in turn.Tokens)
                    frequencies[term] = frequencies.GetValueOrDefault(term) + 1;

                double score = 0;
                foreach (var term in queryTokens)
                {
                    if (!frequencies.TryGetValue(term, out var frequency)) continue;
                    var idf = Math.Log(1 + (count - documentFrequency[term] + 0.5) / (documentFrequency[term] + 0.5));
                    score += idf * frequency * (Bm25K1 + 1)
                             / (frequency + Bm25K1 * (1 - Bm25B + Bm25B * turn.Tokens.Length / averageLength));
                }

                if (score > 0) scored.Add((turn, score));
            }

            if (scored.Count == 0) return results;

            // 相对阈值：只保留接近最佳命中的几条，避免把弱相关的旧对话也塞进提示词
            var best = scored.Max(s => s.Score);
            foreach (var item in scored
                         .Where(s => s.Score >= best * 0.5)
                         .OrderByDescending(s => s.Score)
                         .Take(SearchTopK))
                results.Add(new MemoryTurn(item.Turn.Time, item.Turn.Role, item.Turn.Text));
        }

        return results;
    }

    private void LoadTurns()
    {
        try
        {
            if (!File.Exists(_conversationsPath)) return;

            var lines = File.ReadLines(_conversationsPath)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .TakeLast(MaxTurnsInMemory);

            foreach (var line in lines)
            {
                try
                {
                    var turn = JsonSerializer.Deserialize<Turn>(line, LineJsonOptions);
                    if (turn is { Text: { Length: > 0 } }) _turns.Add(turn);
                }
                catch (JsonException)
                {
                    // 跳过损坏行，不让一条坏记录导致整个记忆失效
                }
            }

            TrimConversationFile();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "load conversations failed");
        }
    }

    private void LoadProfile()
    {
        try
        {
            if (!File.Exists(_profilePath)) return;

            var entries = JsonSerializer.Deserialize<List<MemoryEntry>>(File.ReadAllText(_profilePath), JsonOptions);
            if (entries is null) return;

            _profile =
            [
                .. entries
                    .Where(e => !string.IsNullOrWhiteSpace(e.Key))
                    .OrderByDescending(e => e.UpdatedAt)
                    .Take(MaxProfileEntries)
            ];
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "load profile failed");
        }
    }

    private void SaveProfile()
    {
        try
        {
            File.WriteAllText(_profilePath, JsonSerializer.Serialize(_profile, JsonOptions));
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "save profile failed");
        }
    }

    /// <summary>文件过大时重写成内存里的最近若干轮。</summary>
    private void TrimConversationFile()
    {
        try
        {
            if (!File.Exists(_conversationsPath)) return;
            if (new FileInfo(_conversationsPath).Length <= MaxConversationFileBytes) return;

            var lines = _turns.Select(turn => JsonSerializer.Serialize(turn, LineJsonOptions)).ToArray();
            File.WriteAllLines(_conversationsPath, lines);
            Logger.Information("conversations trimmed to {Count} turns", lines.Length);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "trim conversations failed");
        }
    }

    private static bool IsUserRole(string role) =>
        string.Equals(role, UserRole, StringComparison.OrdinalIgnoreCase);

    private static bool IsToolRole(string role) =>
        string.Equals(role, ToolRole, StringComparison.OrdinalIgnoreCase);

    private static string RoleLabel(string role) =>
        IsUserRole(role) ? "用户" : IsToolRole(role) ? "工具" : "你";

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    /// <summary>中文按 2-gram 切分，英文/数字按连续词切分；零依赖，够用且召回稳定。</summary>
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var word = new StringBuilder();
        var cjkRun = new StringBuilder();

        void FlushCjk()
        {
            if (cjkRun.Length == 1) tokens.Add(cjkRun.ToString());
            else
                for (var i = 0; i + 1 < cjkRun.Length; i++)
                    tokens.Add(cjkRun.ToString(i, 2));
            cjkRun.Clear();
        }

        void FlushWord()
        {
            if (word.Length == 0) return;
            tokens.Add(word.ToString().ToLowerInvariant());
            word.Clear();
        }

        foreach (var ch in text)
        {
            if (IsCjk(ch))
            {
                FlushWord();
                cjkRun.Append(ch);
            }
            else if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                FlushCjk();
                word.Append(ch);
            }
            else
            {
                FlushCjk();
                FlushWord();
            }
        }

        FlushCjk();
        FlushWord();
        return tokens;
    }

    private static bool IsCjk(char ch) =>
        ch is >= '\u4E00' and <= '\u9FFF'
            or >= '\u3400' and <= '\u4DBF'
            or >= '\uF900' and <= '\uFAFF';

    /// <summary>落盘用的单轮记录。</summary>
    private sealed class Turn
    {
        public DateTimeOffset Time { get; set; }

        public string Role { get; set; } = "user";

        public string Text { get; set; } = string.Empty;

        /// <summary>检索用的切词结果，只在内存里保留。</summary>
        [JsonIgnore]
        public string[] Tokens { get; set; } = [];

        public void EnsureTokens()
        {
            if (Tokens.Length == 0) Tokens = [.. Tokenize(Text)];
        }
    }
}