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
/// 会话索引文件 sessions.json 的落盘结构。字段只增不改（契约 §7）：
/// 页面/迁移都按可选字段读取，缺字段用默认值补。
/// </summary>
public sealed class ChatSessionIndex
{
    public string? ActiveSessionId { get; set; }

    public List<SessionRecord> Sessions { get; set; } = [];
}

/// <summary>
/// 会话存储：应用数据根目录下的 sessions.json 索引 + sessions/&lt;id&gt;/（conversation.jsonl / window.json），
/// 并提供旧 conversations.jsonl 的一次性迁移与索引损坏时的目录重建。
/// </summary>
public sealed class ChatSessionStore
{
    private static readonly ILogger Logger = Log.ForContext<ChatSessionStore>();

    private const string IndexFileName = "sessions.json";
    private const string SessionsDirectoryName = "sessions";
    private const string ConversationFileName = "conversation.jsonl";
    private const string DeletedDirectoryName = ".deleted";
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

    /// <summary>默认存储：%AppData%\SoufflePet（索引 sessions.json，会话目录 sessions\&lt;id&gt;）。</summary>
    public static ChatSessionStore CreateDefault() => new(
        ChatMemory.DefaultRoot,
        Path.Combine(ChatMemory.DefaultRoot, "profile.json"),
        Path.Combine(ChatMemory.DefaultRoot, "conversations.jsonl"));

    /// <param name="root">应用数据根目录；索引与会话目录都在其下。</param>
    /// <param name="profilePath">全局用户档案；会话之间共享。</param>
    /// <param name="legacyConversationsPath">旧版 conversations.jsonl；仅首次迁移时读取。</param>
    public ChatSessionStore(string root, string? profilePath = null, string? legacyConversationsPath = null)
    {
        Root = Path.GetFullPath(root);
        ProfilePath = profilePath ?? Path.Combine(Root, "profile.json");
        LegacyConversationsPath = legacyConversationsPath
            ?? Path.Combine(Root, "conversations.jsonl");
    }

    public string Root { get; }

    public string ProfilePath { get; }

    public string LegacyConversationsPath { get; }

    public string IndexPath => Path.Combine(Root, IndexFileName);

    /// <summary>所有会话目录的父目录（sessions/，删除回收站也在其下）。</summary>
    public string SessionsRoot => Path.Combine(Root, SessionsDirectoryName);

    public string SessionDirectory(string id) => Path.Combine(SessionsRoot, id);

    public string ConversationPath(string id) => Path.Combine(SessionDirectory(id), ConversationFileName);

    public static string NewSessionId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// 读取索引；不存在时先迁移旧 conversations.jsonl，再按目录重建，最后兜底新建一个空会话。
    /// 索引损坏不会丢会话：直接按会话目录重建。
    /// </summary>
    public ChatSessionIndex Load()
    {
        Directory.CreateDirectory(Root);

        ChatSessionIndex? index = null;
        if (File.Exists(IndexPath))
        {
            try
            {
                index = JsonSerializer.Deserialize<ChatSessionIndex>(File.ReadAllText(IndexPath), JsonOptions);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                Logger.Warning(ex, "sessions.json corrupted; rebuilding from session directories");
            }
        }

        var records = index?.Sessions?
            .Where(record => record is not null && IsSafeId(record.Id))
            .GroupBy(record => record.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList() ?? [];

        if (records.Count == 0)
        {
            // 索引丢失但会话目录还在：优先按目录重建，避免把旧对话再迁移一份造成重复会话。
            var rebuilt = RebuildFromDirectories();
            if (rebuilt.Count > 0) records.AddRange(rebuilt);
            else if (MigrateLegacy() is { } migrated) records.Add(migrated);
        }

        // 会话目录可能被手工删掉：索引里还在的条目按空会话补目录；目录没有对话文件时也补上。
        var alive = new List<SessionRecord>(records.Count);
        foreach (var record in records)
        {
            try
            {
                Directory.CreateDirectory(SessionDirectory(record.Id));
                var path = ConversationPath(record.Id);
                if (!File.Exists(path)) File.WriteAllText(path, string.Empty);
                alive.Add(record);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "session directory unavailable id={Id}", record.Id);
            }
        }

        if (alive.Count == 0) alive.Add(CreateEmptyRecord(out _));

        var active = alive.Any(record => string.Equals(record.Id, index?.ActiveSessionId, StringComparison.Ordinal))
            ? index!.ActiveSessionId
            : alive.OrderByDescending(record => record.UpdatedAt).First().Id;

        var result = new ChatSessionIndex { ActiveSessionId = active, Sessions = alive };
        Save(result);
        return result;
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
            Logger.Warning(ex, "save sessions.json failed");
        }
    }

    /// <summary>新建一个空会话目录并返回它的索引记录。</summary>
    public SessionRecord CreateEmptyRecord(out string directory)
    {
        var id = NewSessionId();
        directory = SessionDirectory(id);
        Directory.CreateDirectory(directory);
        var conversation = ConversationPath(id);
        if (!File.Exists(conversation)) File.WriteAllText(conversation, string.Empty);

        var now = DateTimeOffset.Now;
        return new SessionRecord(id, DefaultTitle, null, now, now, 0, null, null);
    }

    /// <summary>删除会话：目录移入 sessions/.deleted/&lt;id&gt;-&lt;时间戳&gt;，可人工恢复。</summary>
    public bool Delete(string id)
    {
        if (!IsSafeId(id)) return false;

        var source = SessionDirectory(id);
        if (!Directory.Exists(source)) return false;

        try
        {
            var trash = Path.Combine(SessionsRoot, DeletedDirectoryName);
            Directory.CreateDirectory(trash);
            var target = Path.Combine(trash, $"{id}-{DateTime.Now:yyyyMMddHHmmssfff}");
            for (var suffix = 2; Directory.Exists(target); suffix++)
                target = Path.Combine(trash, $"{id}-{DateTime.Now:yyyyMMddHHmmssfff}-{suffix}");

            Directory.Move(source, target);
            Logger.Information("session moved to recycle bin id={Id} target={Target}", id, target);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "delete session failed id={Id}", id);
            return false;
        }
    }

    /// <summary>按会话目录重建索引：索引损坏或手工拷贝会话目录后可恢复列表与标题。</summary>
    public IReadOnlyList<SessionRecord> RebuildFromDirectories()
    {
        var records = new List<SessionRecord>();
        if (!Directory.Exists(SessionsRoot)) return records;

        foreach (var directory in Directory.EnumerateDirectories(SessionsRoot))
        {
            var id = Path.GetFileName(directory);
            if (!IsSafeId(id)) continue;

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
                null,
                created,
                updated,
                lines.Count,
                lastUser?.Text,
                model));
        }

        return records;
    }

    /// <summary>首次启动时把旧 conversations.jsonl 复制成默认会话，旧文件保留只读备份。</summary>
    private SessionRecord? MigrateLegacy()
    {
        try
        {
            if (!File.Exists(LegacyConversationsPath)) return null;

            var backup = LegacyConversationsPath + ".bak";
            if (!File.Exists(backup)) File.Copy(LegacyConversationsPath, backup, overwrite: false);

            var id = NewSessionId();
            var directory = SessionDirectory(id);
            Directory.CreateDirectory(directory);
            var target = ConversationPath(id);
            File.Copy(LegacyConversationsPath, target, overwrite: false);

            var lines = TryReadLines(target);
            var firstUser = lines.FirstOrDefault(line => IsUser(line.Role) && line.Text.Length > 0);
            var lastUser = lines.LastOrDefault(line => IsUser(line.Role) && line.Text.Length > 0);
            var model = lines.LastOrDefault(line => line.Model is { Length: > 0 })?.Model;
            var now = DateTimeOffset.Now;
            var updated = File.GetLastWriteTimeUtc(target);

            Logger.Information("legacy conversations migrated id={Id} turns={Count}", id, lines.Count);
            return new SessionRecord(
                id,
                MakeTitle(firstUser?.Text) ?? DefaultTitle,
                null,
                now,
                updated == default ? now : updated,
                lines.Count,
                lastUser?.Text,
                model);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "migrate legacy conversations failed");
            return null;
        }
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