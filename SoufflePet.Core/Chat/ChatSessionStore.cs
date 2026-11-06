// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using SoufflePet.Services;

namespace SoufflePet.Core.Chat;

/// <summary>
/// 会话索引文件 index.json 的落盘结构。字段只增不改（契约 §7）：
/// 页面按可选字段读取，缺字段用默认值补。
/// </summary>
public sealed class ChatSessionIndex
{
    public string? ActiveSessionId { get; set; }

    public List<SessionRecord> Sessions { get; set; } = [];
}

/// <summary>
/// 会话存储：<c>~/.souffle/sessions/</c> 下按工作区分组的会话目录
/// （<c>&lt;工作区&gt;/&lt;id&gt;/conversation.jsonl</c>）+ 根目录的 <c>index.json</c> 索引。
/// 索引损坏时按会话目录重建；不做任何旧数据迁移。
/// </summary>
public sealed class ChatSessionStore
{
    private static readonly ILogger Logger = Log.ForContext<ChatSessionStore>();

    private const string IndexFileName = "index.json";
    private const string ConversationFileName = "conversation.jsonl";
    private const string GroupMarkerFileName = "workspace.txt";
    private const string DeletedDirectoryName = ".deleted";
    private const string DefaultGroupName = "_default";
    private const string DefaultTitle = "新会话";
    private const int TitleLength = 30;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>默认存储：用户目录 <c>~/.souffle/sessions</c>（按工作区分组）。</summary>
    public static ChatSessionStore CreateDefault() => new(
        Path.Combine(ChatMemory.SouffleHome, "sessions"),
        Path.Combine(ChatMemory.SouffleHome, "memory.json"));

    /// <param name="root">会话存储根目录（索引、按工作区分组的会话目录、回收站都在其下）。</param>
    /// <param name="profilePath">全局用户档案；会话之间共享。</param>
    public ChatSessionStore(string root, string? profilePath = null)
    {
        Root = Path.GetFullPath(root);
        ProfilePath = profilePath ?? Path.Combine(Root, "memory.json");
    }

    /// <summary>会话存储根目录。</summary>
    public string Root { get; }

    public string ProfilePath { get; }

    public string IndexPath => Path.Combine(Root, IndexFileName);

    /// <summary>回收站目录（会话删除后移入这里）。</summary>
    public string TrashDirectory => Path.Combine(Root, DeletedDirectoryName);

    /// <summary>工作区分组目录名：可读前缀 + 路径哈希，避免同名目录冲突。</summary>
    public static string WorkspaceGroupName(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath)) return DefaultGroupName;

        string full;
        try
        {
            full = Path.GetFullPath(workspacePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            full = workspacePath;
        }

        full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var leaf = Path.GetFileName(full);
        if (string.IsNullOrEmpty(leaf)) leaf = full.Replace(':', '_');

        var safe = new string(leaf.Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray()).Trim('_');
        if (safe.Length == 0) safe = "workspace";
        if (safe.Length > 40) safe = safe[..40];

        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..8].ToLowerInvariant();
        return $"{safe}-{hash}";
    }

    /// <summary>某个工作区对应的分组目录。</summary>
    public string GroupDirectory(string? workspacePath) =>
        Path.Combine(Root, WorkspaceGroupName(workspacePath));

    public string SessionDirectory(string id, string? workspacePath = null) =>
        Path.Combine(GroupDirectory(workspacePath), id);

    public string ConversationPath(string id, string? workspacePath = null) =>
        Path.Combine(SessionDirectory(id, workspacePath), ConversationFileName);

    public static string NewSessionId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// 读取索引；索引丢失时按会话目录重建。索引损坏不会丢会话。
    /// </summary>
    public ChatSessionIndex Load()
    {
        Directory.CreateDirectory(Root);

        var index = ReadIndex(IndexPath);
        var records = index?.Sessions?
            .Where(record => record is not null && IsSafeId(record.Id))
            .GroupBy(record => record.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList() ?? [];

        if (records.Count == 0)
        {
            // 索引丢失但会话目录还在：按目录重建
            records.AddRange(RebuildFromDirectories());
        }

        // 会话目录可能被手工删掉/换过工作区：索引里还在的条目按工作区分组补齐或搬过去。
        var alive = new List<SessionRecord>(records.Count);
        foreach (var record in records)
        {
            try
            {
                EnsureRecordDirectory(record);
                alive.Add(record);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "session directory unavailable id={Id}", record.Id);
            }
        }

        if (alive.Count == 0)
        {
            // 没有已落盘会话时不伪造空会话：由 ChatSessionManager 在内存里开一个草稿，
            // 真正发出第一条消息时才写索引与目录。
            return new ChatSessionIndex { ActiveSessionId = null, Sessions = [] };
        }

        var active = alive.Any(record => string.Equals(record.Id, index?.ActiveSessionId, StringComparison.Ordinal))
            ? index!.ActiveSessionId
            : alive.OrderByDescending(record => record.UpdatedAt).First().Id;

        var result = new ChatSessionIndex { ActiveSessionId = active, Sessions = alive };
        Save(result);
        return result;
    }

    /// <summary>把索引里的会话目录放到它当前工作区对应的分组下（工作区被外部改过时搬过去）。</summary>
    private void EnsureRecordDirectory(SessionRecord record)
    {
        var desired = SessionDirectory(record.Id, record.WorkspacePath);
        if (!Directory.Exists(desired))
        {
            var found = FindSessionDirectory(record.Id);
            if (found is not null && !SamePath(found, desired))
            {
                MoveDirectory(found, desired);
            }
            else
            {
                Directory.CreateDirectory(desired);
            }
        }

        WriteGroupMarker(record.WorkspacePath);

        var conversation = Path.Combine(desired, ConversationFileName);
        if (!File.Exists(conversation)) File.WriteAllText(conversation, string.Empty);
    }

    /// <summary>在所有工作区分组里找某个会话的目录。</summary>
    private string? FindSessionDirectory(string id)
    {
        foreach (var group in Directory.EnumerateDirectories(Root))
        {
            if (string.Equals(Path.GetFileName(group), DeletedDirectoryName, StringComparison.Ordinal)) continue;
            var candidate = Path.Combine(group, id);
            if (Directory.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>确保分组目录存在，并在里面记下工作区全路径（重建索引时用来还原分组）。</summary>
    private void WriteGroupMarker(string? workspacePath)
    {
        try
        {
            var group = GroupDirectory(workspacePath);
            Directory.CreateDirectory(group);
            var marker = Path.Combine(group, GroupMarkerFileName);
            if (File.Exists(marker)) return;
            File.WriteAllText(marker, string.IsNullOrWhiteSpace(workspacePath)
                ? string.Empty
                : Path.GetFullPath(workspacePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warning(ex, "write workspace marker failed path={Path}", workspacePath);
        }
    }

    /// <summary>确保工作区分组目录与标记存在（会话内容由 ChatMemory 按需写入）。</summary>
    public void EnsureGroup(string? workspacePath) => WriteGroupMarker(workspacePath);

    private static string? ReadGroupMarker(string group)
    {
        try
        {
            var marker = Path.Combine(group, GroupMarkerFileName);
            if (!File.Exists(marker)) return null;
            var text = File.ReadAllText(marker).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ChatSessionIndex? ReadIndex(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<ChatSessionIndex>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Logger.Warning(ex, "session index corrupted path={Path}", path);
            return null;
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static void MoveDirectory(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (Directory.Exists(target)) target = Path.Combine(target, Path.GetFileName(source));
        try
        {
            Directory.Move(source, target);
        }
        catch (IOException)
        {
            // 跨卷：复制后删除
            CopyDirectory(source, target);
            Directory.Delete(source, recursive: true);
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    /// <summary>原子写入索引（先写临时文件再替换，避免断电留下半个 JSON）。</summary>
    public void Save(ChatSessionIndex index)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var temp = IndexPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(index, JsonOptions));
            File.Move(temp, IndexPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "save session index failed");
        }
    }

    /// <summary>
    /// 生成一条会话记录但不在磁盘上创建任何东西（草稿会话：首条消息发出时才落盘）。
    /// </summary>
    public SessionRecord NewDraftRecord(string? workspacePath)
    {
        var now = DateTimeOffset.Now;
        return new SessionRecord(NewSessionId(), DefaultTitle, workspacePath, now, now, 0, null, null);
    }

    /// <summary>
    /// 删除草稿会话的残留目录：草稿从未写入过索引/对话，直接删目录，不进回收站。
    /// 目录不存在时静默返回。
    /// </summary>
    public void DeleteDraft(string id, string? workspacePath = null)
    {
        if (!IsSafeId(id)) return;
        try
        {
            var directory = SessionDirectory(id, workspacePath);
            if (!Directory.Exists(directory)) directory = FindSessionDirectory(id);
            if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "delete draft directory failed id={Id}", id);
        }
    }

    /// <summary>删除会话：目录移入 &lt;root&gt;/.deleted/&lt;id&gt;-&lt;时间戳&gt;，可人工恢复。</summary>
    public bool Delete(SessionRecord record)
    {
        if (!IsSafeId(record.Id)) return false;

        var source = SessionDirectory(record.Id, record.WorkspacePath);
        if (!Directory.Exists(source)) source = FindSessionDirectory(record.Id);
        if (source is null || !Directory.Exists(source)) return false;

        try
        {
            Directory.CreateDirectory(TrashDirectory);
            var target = Path.Combine(TrashDirectory, $"{record.Id}-{DateTime.Now:yyyyMMddHHmmssfff}");
            for (var suffix = 2; Directory.Exists(target); suffix++)
                target = Path.Combine(TrashDirectory, $"{record.Id}-{DateTime.Now:yyyyMMddHHmmssfff}-{suffix}");

            Directory.Move(source, target);
            Logger.Information("session moved to recycle bin id={Id} target={Target}", record.Id, target);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "delete session failed id={Id}", record.Id);
            return false;
        }
    }

    /// <summary>按会话目录重建索引：索引损坏或手工拷贝会话目录后可恢复列表与标题。</summary>
    public IReadOnlyList<SessionRecord> RebuildFromDirectories()
    {
        var records = new List<SessionRecord>();
        if (!Directory.Exists(Root)) return records;

        foreach (var group in Directory.EnumerateDirectories(Root))
        {
            var name = Path.GetFileName(group);
            if (string.Equals(name, DeletedDirectoryName, StringComparison.Ordinal)) continue;

            var workspace = ReadGroupMarker(group);
            foreach (var directory in Directory.EnumerateDirectories(group))
            {
                var id = Path.GetFileName(directory);
                if (!IsSafeId(id)) continue;
                AddRecord(records, directory, id, workspace);
            }
        }

        return records;
    }

    private static void AddRecord(
        List<SessionRecord> records,
        string directory,
        string id,
        string? workspacePath)
    {
        var conversation = Path.Combine(directory, ConversationFileName);
        var lines = TryReadLines(conversation);
        var firstUser = lines.FirstOrDefault(line => IsUser(line.Role) && line.Text.Length > 0);
        var lastUser = lines.LastOrDefault(line => IsUser(line.Role) && line.Text.Length > 0);
        var model = lines.LastOrDefault(line => line.Model is { Length: > 0 })?.Model;

        DateTimeOffset created;
        DateTimeOffset updated;
        try
        {
            created = new DirectoryInfo(directory).CreationTimeUtc;
            updated = File.Exists(conversation) ? File.GetLastWriteTimeUtc(conversation) : created;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            created = updated = DateTimeOffset.Now;
        }

        records.Add(new SessionRecord(
            id,
            MakeTitle(firstUser?.Text) ?? DefaultTitle,
            workspacePath,
            created,
            updated,
            lines.Count,
            lastUser?.Text,
            model));
    }

    private static List<ConversationLine> TryReadLines(string path)
    {
        var result = new List<ConversationLine>();
        if (!File.Exists(path)) return result;

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<ConversationLine>(line) is { Text.Length: > 0 } turn)
                        result.Add(turn);
                }
                catch (JsonException)
                {
                    // 跳过损坏行，不影响其余记录
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warning(ex, "read conversation failed path={Path}", path);
        }

        return result;
    }

    private static bool IsUser(string? role) =>
        string.Equals(role, ChatMemory.UserRole, StringComparison.OrdinalIgnoreCase);

    /// <summary>标题取首条用户消息的第一行，最多 30 字；空内容返回 null 由调用方兜底。</summary>
    private static string? MakeTitle(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= TitleLength ? line : line[..TitleLength] + "…";
    }

    /// <summary>会话 id 只允许字母/数字/连字符/下划线，避免索引里出现路径穿越。</summary>
    public static bool IsSafeId(string? id) =>
        !string.IsNullOrEmpty(id)
        && id.Length <= 64
        && id.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_');

    private sealed class ConversationLine
    {
        public DateTimeOffset Time { get; set; }

        public string Role { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;

        public string? Model { get; set; }
    }
}