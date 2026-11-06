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
using SoufflePet.Core.Chat;
using SoufflePet.Models;

namespace SoufflePet.Services;

/// <summary>恢复界面用的一轮历史对话；工具记录的 CallId / ToolName / Arguments / Result 用于重建请求上下文。</summary>
public readonly record struct MemoryTurn(
    DateTimeOffset Time,
    string Role,
    string Text,
    string? CallId = null,
    string? ToolName = null,
    string? Arguments = null,
    string? Result = null,
    string? Reasoning = null,
    string? Display = null,
    bool IsPlatformNotice = false,
    string? RequestText = null,
    long? InputTokens = null,
    long? OutputTokens = null,
    long? CachedInputTokens = null,
    string? Model = null,
    string? Label = null,
    IReadOnlyList<ChatFileDiffState>? Diffs = null);

/// <summary>
/// 长期记忆：对话落盘、用户档案与关键词检索。
/// 用户级数据统一保存在 <c>~/.souffle</c> 下（不随安装目录变化）：
/// 会话目录里的 conversation.jsonl 记录每轮问答与工具调用，memory.json 保存模型用 remember 写入的事实。
/// 检索使用中文 2-gram + BM25，不依赖分词库与向量服务。
/// </summary>
public sealed class ChatMemory
{
    /// <summary>用户角色。</summary>
    public const string UserRole = "user";

    /// <summary>助手角色。</summary>
    public const string AssistantRole = "assistant";

    /// <summary>工具调用记录（参与检索；启动时按标准 tool 消息重建请求上下文）。</summary>
    public const string ToolRole = "tool";

    /// <summary>内存里保留的最大历史轮数，也是启动时读取的行数。</summary>
    private const int MaxTurnsInMemory = 2000;

    /// <summary>档案条数上限，超出丢弃最久未更新的。</summary>
    private const int MaxProfileEntries = 30;

    /// <summary>一次检索最多注入几条回忆。</summary>
    private const int SearchTopK = 3;

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
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Lock _gate = new();
    private readonly SouffleSettings _settings;
    private string _root;
    private string _conversationsPath;
    private string _profilePath;
    private string _windowPath;
    private readonly List<Turn> _turns = [];
    private List<MemoryEntry> _profile = [];
    private int _windowTurns;

    /// <summary>
    /// 用户级数据根目录：默认 <c>%USERPROFILE%\.souffle</c>，长期档案、统计、调度日志、窗口几何、附件与
    /// <c>sessions/</c> 都在这里；<c>mcp.json</c>、<c>plugins/</c>、<c>skills/</c> 同样落在其下。
    /// 可用环境变量 <c>SOUFFLEPET_DATA_DIR</c> 指向独立目录，供自动化验收/测试隔离用户数据。
    /// </summary>
    public static string SouffleHome
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("SOUFFLEPET_DATA_DIR");
            return string.IsNullOrWhiteSpace(custom)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".souffle")
                : Path.GetFullPath(custom);
        }
    }

    public ChatMemory(SouffleSettings settings)
        : this(settings, SouffleHome, "conversations.jsonl", Path.Combine(SouffleHome, "memory.json"))
    {
    }

    /// <summary>测试用：把会话/档案/窗口状态写到指定根目录，避免污染用户数据。</summary>
    internal ChatMemory(SouffleSettings settings, string root)
        : this(settings, root, "conversations.jsonl", Path.Combine(root, "memory.json"))
    {
    }

    /// <summary>
    /// 会话级记忆：对话文件放在会话目录里（默认 conversation.jsonl），档案仍指向全局 memory.json，
    /// 这样每个会话有独立上下文，而 remember 写入的长期档案对所有会话共享。
    /// </summary>
    internal ChatMemory(SouffleSettings settings, string root, string conversationFileName, string profilePath)
    {
        _settings = settings;

        // 目录按需创建：草稿会话在发出第一条消息前不应该在磁盘上留下任何东西。
        _root = root;
        _conversationsPath = Path.Combine(root, conversationFileName);
        _profilePath = profilePath;
        _windowPath = Path.Combine(root, "window.json");

        LoadProfile();
        LoadTurns();
        LoadWindow();
    }

    /// <summary>会话目录：conversation.jsonl / window.json / diffs / backups 都在这里。</summary>
    public string SessionDirectory => _root;

    /// <summary>会话目录；首次写入时才真正创建（草稿会话保持在纯内存状态）。</summary>
    private void EnsureDirectory()
    {
        try
        {
            if (!Directory.Exists(_root)) Directory.CreateDirectory(_root);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "create session directory failed path={Path}", _root);
        }
    }

    /// <summary>
    /// 会话换工作区时把落盘目录一起搬走（会话按工作区分组存放）。
    /// 已写过的 conversation.jsonl / window.json 随之移动，之后继续写新位置。
    /// </summary>
    public void Rebase(string root)
    {
        string target;
        try
        {
            target = Path.GetFullPath(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        lock (_gate)
        {
            if (string.Equals(_root, target, StringComparison.OrdinalIgnoreCase)) return;

            var source = _root;
            try
            {
                Directory.CreateDirectory(target);
                MoveFileIfExists(_conversationsPath, Path.Combine(target, Path.GetFileName(_conversationsPath)));
                MoveFileIfExists(_windowPath, Path.Combine(target, "window.json"));
                MoveDirectoryIfExists(Path.Combine(source, "diffs"), Path.Combine(target, "diffs"));
                MoveDirectoryIfExists(Path.Combine(source, "backups"), Path.Combine(target, "backups"));

                // 旧目录空了就顺手删掉，避免工作区切换留下空壳
                if (Directory.Exists(source) && !Directory.EnumerateFileSystemEntries(source).Any())
                    Directory.Delete(source);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "rebase session directory failed from={From} to={To}", source, target);
            }

            _root = target;
            _conversationsPath = Path.Combine(target, Path.GetFileName(_conversationsPath));
            _windowPath = Path.Combine(target, "window.json");
        }
    }

    private static void MoveDirectoryIfExists(string from, string to)
    {
        if (!Directory.Exists(from) || string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.EnumerateFiles(from))
                File.Move(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);

            if (!Directory.EnumerateFileSystemEntries(from).Any()) Directory.Delete(from);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warning(ex, "move session diff directory failed from={From} to={To}", from, to);
        }
    }

    private static void MoveFileIfExists(string from, string to)
    {
        if (!File.Exists(from) || string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            File.Move(from, to, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warning(ex, "move session file failed from={From} to={To}", from, to);
        }
    }

    /// <summary>记忆总开关（设置里可关掉注入，已落盘的内容保留）。</summary>
    private bool Enabled => _settings.ChatMemoryEnabled;

    /// <summary>
    /// 追加一条记录并落盘；role 为 user / assistant / tool。
    /// tool 记录额外保存调用信息（CallId、工具名、参数、完整结果），供启动时重建请求上下文。
    /// requestText 是真正发给模型的正文（含时间 / 回忆等附加内容）；与展示用的 text 分开保存，
    /// 重启后按它重建请求历史，第一次请求才能接上上一轮的前缀缓存。
    /// inputTokens / outputTokens / cachedInputTokens 是本轮 token 用量（只记在最后一条助手消息上），
    /// 重启后据此恢复气泡底部的用量文案。
    /// </summary>
    public void AddTurn(
        string role,
        string text,
        string? callId = null,
        string? toolName = null,
        string? arguments = null,
        string? result = null,
        string? reasoning = null,
        string? display = null,
        bool isPlatformNotice = false,
        string? requestText = null,
        long? inputTokens = null,
        long? outputTokens = null,
        long? cachedInputTokens = null,
        string? model = null,
        string? toolLabel = null,
        IReadOnlyList<ChatFileDiffState>? diffs = null)
    {
        // 正文统一去空白：纯空白视作无正文，只有存在思考内容时才继续落库
        text = text.Trim();
        if (text.Length == 0 && string.IsNullOrWhiteSpace(reasoning)) return;

        var turn = new Turn
        {
            Time = DateTimeOffset.Now,
            Role = role,
            Text = text,
            RequestText = string.IsNullOrWhiteSpace(requestText) ? null : requestText,
            CallId = callId,
            ToolName = toolName,
            Arguments = arguments,
            Result = result,
            Reasoning = string.IsNullOrWhiteSpace(reasoning) ? null : reasoning.Trim(),
            Display = string.IsNullOrWhiteSpace(display) ? null : display.Trim(),
            IsPlatformNotice = isPlatformNotice,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CachedInputTokens = cachedInputTokens,
            Model = string.IsNullOrWhiteSpace(model) ? null : model,
            Label = string.IsNullOrWhiteSpace(toolLabel) ? null : toolLabel.Trim()
        };
        lock (_gate)
        {
            // 完整 diff 单独存文件：JSONL 只留引用，避免多次编辑后会话文件膨胀、拖慢启动
            if (diffs is { Count: > 0 }) turn.DiffFile = SaveDiffFileLocked(diffs);

            _turns.Add(turn);
            if (_turns.Count > MaxTurnsInMemory)
                _turns.RemoveRange(0, _turns.Count - MaxTurnsInMemory);

            try
            {
                EnsureDirectory();
                File.AppendAllText(_conversationsPath,
                    JsonSerializer.Serialize(turn, LineJsonOptions) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "append conversation failed");
            }
        }
    }

    /// <summary>
    /// 把本轮用量补记到本轮最近一条助手记录上：本轮只以工具调用收尾、没有正文可落库时使用，
    /// 避免已经花掉的 token 在对话记录里留空（重启后气泡下方的用量胶囊也据此恢复）。
    /// since 是本轮开始时间，用来把范围限定在本轮；本轮没有助手正文（只有工具记录）时退而记在本轮最后一条记录上。
    /// </summary>
    public void AttachUsageToLastTurn(
        DateTimeOffset since, long inputTokens, long outputTokens, long cachedInputTokens, string? model)
    {
        lock (_gate)
        {
            // 记录按时间顺序追加：本轮第一条记录的下标
            var first = _turns.FindLastIndex(turn => turn.Time < since) + 1;
            if (first >= _turns.Count) return;

            // 优先挂到最近一条助手正文（用量胶囊就在它下方），只有工具记录时退而挂到本轮最后一条
            var index = -1;
            for (var i = _turns.Count - 1; i >= first; i--)
            {
                if (IsUserRole(_turns[i].Role) || IsToolRole(_turns[i].Role)) continue;
                index = i;
                break;
            }

            if (index < 0) index = _turns.Count - 1;

            var turn = _turns[index];
            turn.InputTokens = inputTokens;
            turn.OutputTokens = outputTokens;
            turn.CachedInputTokens = cachedInputTokens;
            turn.Model = string.IsNullOrWhiteSpace(model) ? null : model;

            try
            {
                if (!File.Exists(_conversationsPath)) return;
                // conversations.jsonl 是追加写的，内存里的 _turns 对应文件末尾若干行：
                // 定位到这一条改写后原样写回（只在个别收尾场景补记，不会频繁发生）。
                var lines = File.ReadAllLines(_conversationsPath);
                var fileIndex = lines.Length - (_turns.Count - index);
                if (fileIndex < 0 || fileIndex >= lines.Length) return;
                lines[fileIndex] = JsonSerializer.Serialize(turn, LineJsonOptions);
                File.WriteAllLines(_conversationsPath, lines);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "attach usage to conversation failed");
            }
        }
    }

    /// <summary>
    /// 最近若干轮对话（用于界面恢复与上下文重建）；includeTools 为 true 时一并返回工具调用记录。
    /// 一轮以用户提问为边界，包含该提问及其后的助手回复与工具记录；不足 maxTurns 轮时返回全部。
    /// </summary>
    public IReadOnlyList<MemoryTurn> RecentTurns(int maxTurns, bool includeTools = false)
    {
        lock (_gate)
        {
            // 从后往前数到第 maxTurns 个用户提问，窗口从这里开始，
            // 避免按条数取窗口时把一轮对话截成半截（工具链尤其容易超长）
            var start = 0;
            var userTurns = 0;
            for (var i = _turns.Count - 1; i >= 0; i--)
            {
                if (!IsUserRole(_turns[i].Role)) continue;
                if (++userTurns != maxTurns) continue;
                start = i;
                break;
            }

            var turns = _turns.Skip(start)
                .Where(t => includeTools || !IsToolRole(t.Role))
                .Select(ToMemoryTurn)
                .ToList();

            Logger.Debug("recent turns requested={Max} returned={Count}", maxTurns, turns.Count);
            return turns;
        }
    }

    /// <summary>
    /// 上次会话最后一次请求使用的窗口轮数（0 表示没有记录）。
    /// 重启后按同一轮数恢复历史窗口，第一次请求才能和上次请求的前缀对上、继续命中服务端缓存；
    /// 若固定按最大窗口恢复，首轮请求会把窗口裁短、前缀全变，缓存直接失效。
    /// </summary>
    public int WindowTurns => _windowTurns;

    /// <summary>记录当前请求窗口的轮数（每次对话结束时写盘；窗口只在收到新提问 / 裁剪时变化）。</summary>
    public void SaveWindowTurns(int turns)
    {
        if (turns <= 0 || turns == _windowTurns) return;

        _windowTurns = turns;
        try
        {
            EnsureDirectory();
            File.WriteAllText(_windowPath, JsonSerializer.Serialize(new WindowState { Turns = turns }, JsonOptions));
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "save window state failed");
        }
    }

    private void LoadWindow()
    {
        try
        {
            if (!File.Exists(_windowPath)) return;
            var state = JsonSerializer.Deserialize<WindowState>(File.ReadAllText(_windowPath), JsonOptions);
            if (state is { Turns: > 0 }) _windowTurns = state.Turns;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "load window state failed");
        }
    }

    /// <summary>window.json 的落盘结构。</summary>
    private sealed class WindowState
    {
        public int Turns { get; set; }
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

            SaveProfile(merge: true);
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
            SaveProfile(merge: false);
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
                .AppendLine(turn.Text);
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// 把「用户档案」追加到系统提示词：内容只在 remember / forget 后变化，属于半静态内容，
    /// 放在系统提示词里不会随每轮提问抖动，能保住前缀缓存。
    /// 关闭记忆或档案为空时不追加。
    /// </summary>
    public void BuildProfileBlock(StringBuilder builder)
    {
        if (!Enabled) return;

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

        if (profileLines.Count == 0) return;

        builder.AppendLine();
        builder.AppendLine("## 记忆系统");
        builder.AppendLine();
        builder.AppendLine("使用remember/forget命令来添加/删除记忆，记忆跨会话共享。以下是之前对话里记忆的信息，回答时可以自然使用，不要生硬复述。");
        builder.AppendLine();
        builder.AppendLine("### 长期记忆：");
        builder.AppendLine();
        foreach (var line in profileLines) builder.AppendLine(line);
    }

    /// <summary>
    /// 检索与 <paramref name="query" /> 相关的旧对话，返回逐行文本（每行一条回忆，正文压成单行）。
    /// 这部分内容随每轮提问变化，由调用方追加到请求末尾（而不是系统提示词），
    /// 否则每轮请求的前缀都不同，服务端的前缀缓存会整体失效。
    /// exclude 里的文本与 excludeCallIds 里的调用 id（通常是本次请求已带上的历史对话）不参与检索，
    /// 避免与上下文重复——工具消息的文本为空，只能按 CallId 排除。
    /// 关闭记忆、没有提问或没有命中时返回空串。
    /// </summary>
    public string BuildRecallBlock(
        string? query,
        IReadOnlyCollection<string>? exclude = null,
        IReadOnlyCollection<string>? excludeCallIds = null)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(query)) return string.Empty;

        var snippets = Search(query, exclude, excludeCallIds);
        if (snippets.Count == 0) return string.Empty;

        var builder = new StringBuilder();
        foreach (var turn in snippets)
        {
            builder.Append("- [")
                .Append(turn.Time.ToString("yyyy-MM-dd HH:mm"))
                .Append("] ")
                .Append(RoleLabel(turn.Role))
                .Append('：')
                .AppendLine(turn.Text.ReplaceLineEndings(" "));
        }

        return builder.ToString().TrimEnd();
    }

    private List<MemoryTurn> Search(
        string? query,
        IReadOnlyCollection<string>? exclude = null,
        IReadOnlyCollection<string>? excludeCallIds = null)
    {
        var results = new List<MemoryTurn>();
        if (!Enabled || string.IsNullOrWhiteSpace(query)) return results;

        var queryTokens = Tokenize(query).Distinct(StringComparer.Ordinal).ToArray();
        if (queryTokens.Length == 0) return results;

        // 已进入本次请求上下文的对话不参与检索，否则会与上下文内容重复
        HashSet<string>? excluded = exclude is { Count: > 0 }
            ? [.. exclude.Select(t => t.Trim())]
            : null;

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
                // 召回旧对话时不带工具调用与工具结果：它们只是过程的中间产物，不是「回忆」的内容
                if (IsToolRole(turn.Role)) continue;
                if (excluded is not null && excluded.Contains(turn.Text)) continue;
                if (excludeCallIds is not null && turn.CallId is { Length: > 0 } callId
                    && excludeCallIds.Contains(callId)) continue;

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

    private MemoryTurn ToMemoryTurn(Turn turn) => new(
        turn.Time, turn.Role, turn.Text, turn.CallId, turn.ToolName, turn.Arguments, turn.Result,
        turn.Reasoning, turn.Display, turn.IsPlatformNotice, turn.RequestText,
        turn.InputTokens, turn.OutputTokens, turn.CachedInputTokens, turn.Model, turn.Label,
        turn.DiffFile is { Length: > 0 } file ? LoadDiffFile(file) : null);

    /// <summary>把完整 diff 写到会话目录的 <c>diffs/</c>，返回 JSONL 里保存的文件名（调用方须持有锁）。</summary>
    private string? SaveDiffFileLocked(IReadOnlyList<ChatFileDiffState> diffs)
    {
        try
        {
            EnsureDirectory();
            var directory = Path.Combine(_root, "diffs");
            Directory.CreateDirectory(directory);
            var name = $"{Guid.NewGuid():N}.json";
            File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(diffs, LineJsonOptions));
            return name;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "save conversation diff failed");
            return null;
        }
    }

    /// <summary>按文件名加载 diff；文件缺失或损坏时当作没有 diff，不影响会话恢复。</summary>
    private IReadOnlyList<ChatFileDiffState>? LoadDiffFile(string fileName)
    {
        try
        {
            // 文件名来自会话文件，按不可信输入处理，禁止路径穿越
            if (fileName != Path.GetFileName(fileName)
                || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return null;

            var path = Path.Combine(_root, "diffs", fileName);
            if (!File.Exists(path)) return null;

            return JsonSerializer.Deserialize<List<ChatFileDiffState>>(File.ReadAllText(path), LineJsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Logger.Warning(ex, "load conversation diff failed file={File}", fileName);
            return null;
        }
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

            Logger.Debug("conversation loaded turns={Count}", _turns.Count);
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

    /// <summary>
    /// 落盘用户档案。多会话共享同一个 memory.json 时，<paramref name="merge" /> 先把磁盘上
    /// 其他会话刚写入的条目合并进来（同键以本实例为准），避免后写的会话覆盖先写的记忆；
    /// 删除走 merge: false，确保删除结果就是最终结果。
    /// </summary>
    private void SaveProfile(bool merge)
    {
        try
        {
            if (merge && File.Exists(_profilePath))
            {
                var onDisk = JsonSerializer.Deserialize<List<MemoryEntry>>(
                    File.ReadAllText(_profilePath), JsonOptions);
                if (onDisk is { Count: > 0 })
                {
                    var keys = _profile.Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    _profile.AddRange(onDisk.Where(e =>
                        !string.IsNullOrWhiteSpace(e.Key) && !keys.Contains(e.Key)));
                    if (_profile.Count > MaxProfileEntries)
                    {
                        _profile = _profile
                            .OrderByDescending(e => e.UpdatedAt)
                            .Take(MaxProfileEntries)
                            .ToList();
                    }
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_profilePath)!);
            File.WriteAllText(_profilePath, JsonSerializer.Serialize(_profile, JsonOptions));
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "save profile failed");
        }
    }

    private static bool IsUserRole(string role) =>
        string.Equals(role, UserRole, StringComparison.OrdinalIgnoreCase);

    private static bool IsToolRole(string role) =>
        string.Equals(role, ToolRole, StringComparison.OrdinalIgnoreCase);

    private static string RoleLabel(string role) =>
        IsUserRole(role) ? "用户" : IsToolRole(role) ? "工具" : "你";

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

        /// <summary>真正发给模型的正文（含时间 / 回忆等附加内容）；为空时回退到 <see cref="Text" />。</summary>
        public string? RequestText { get; set; }

        /// <summary>本轮 token 用量：输入（仅记录在本轮最后一条助手消息上，用于重启后恢复气泡底部的用量文案）。</summary>
        public long? InputTokens { get; set; }

        /// <summary>本轮 token 用量：输出。</summary>
        public long? OutputTokens { get; set; }

        /// <summary>本轮 token 用量：缓存命中。</summary>
        public long? CachedInputTokens { get; set; }

        /// <summary>本轮使用的模型（提供方/模型），供气泡的用量浮窗恢复。</summary>
        public string? Model { get; set; }

        /// <summary>工具调用的 id（仅 tool 记录）。</summary>
        public string? CallId { get; set; }

        /// <summary>工具名（仅 tool 记录）。</summary>
        public string? ToolName { get; set; }

        /// <summary>工具参数 JSON（仅 tool 记录）。</summary>
        public string? Arguments { get; set; }

        /// <summary>工具完整结果（仅 tool 记录）。</summary>
        public string? Result { get; set; }

        /// <summary>工具气泡标题（仅 tool 记录），如「运行命令 · npm test」。</summary>
        public string? Label { get; set; }

        /// <summary>
        /// 工具编辑文件产生的差异文件（仅 tool 记录），位于会话目录的 <c>diffs/</c> 下。
        /// JSONL 只存文件名，完整 hunk 单独落盘，避免会话文件随编辑次数膨胀。
        /// </summary>
        public string? DiffFile { get; set; }

        /// <summary>该轮的思考内容（reasoning_content），用于请求时原样回传给推理模型。</summary>
        public string? Reasoning { get; set; }

        /// <summary>界面上展示的文本；与发给模型的 Text 不同时使用（例如平台消息的紧凑摘要）。</summary>
        public string? Display { get; set; }

        /// <summary>该条是否为平台消息。与 <see cref="Display" /> 相互独立：Display 只表示展示文本不同。</summary>
        public bool IsPlatformNotice { get; set; }

        /// <summary>检索用的切词结果，只在内存里保留。</summary>
        [JsonIgnore]
        public string[] Tokens { get; set; } = [];

        public void EnsureTokens()
        {
            if (Tokens.Length == 0) Tokens = [.. Tokenize(Text)];
        }
    }
}