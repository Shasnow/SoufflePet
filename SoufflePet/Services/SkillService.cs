// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog;

namespace SoufflePet.Services;

/// <summary>一个技能：工作目录下 <c>.agents/skills</c> 或 <c>.souffle/skills</c> 中的说明文档。</summary>
public sealed record SkillInfo(string Name, string Description, string Source, string FilePath)
{
    public bool HasDescription => Description.Length > 0;
}

/// <summary>扫描并读取技能文档。技能本身无需注册，扫描目录即可发现。</summary>
public interface ISkillService
{
    /// <summary>扫描根目录：应用目录下的 <c>.agents/skills</c> 与 <c>.souffle/skills</c>。</summary>
    IReadOnlyList<string> SearchDirectories { get; }

    /// <summary>列出全部技能（同名只保留先扫描到的）。目录不存在或文件解析失败只记日志。</summary>
    IReadOnlyList<SkillInfo> Discover();

    /// <summary>读取技能正文（去掉 YAML frontmatter）；文件不存在时返回空字符串。</summary>
    string ReadContent(SkillInfo skill);
}

/// <summary>
/// 技能目录约定：每个技能占一个子目录，内含带 YAML frontmatter（name/description）的 SKILL.md；
/// 也支持直接放在技能目录下的单个 *.md 文件。正文即 SKILL.md 去掉 frontmatter 后的内容。
/// </summary>
public sealed class SkillService : ISkillService
{
    private static readonly ILogger Logger = Log.ForContext<SkillService>();

    public IReadOnlyList<string> SearchDirectories { get; } =
    [
        Path.Combine(AppContext.BaseDirectory, ".agents", "skills"),
        Path.Combine(AppContext.BaseDirectory, ".souffle", "skills")
    ];

    public IReadOnlyList<SkillInfo> Discover()
    {
        var skills = new List<SkillInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in SearchDirectories)
        {
            if (!Directory.Exists(root)) continue;

            try
            {
                // 形如 .agents/skills/<技能名>/SKILL.md
                foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
                {
                    var entry = FindSkillFile(directory);
                    if (entry is null) continue;
                    AddSkill(skills, seen, entry, Describe(root), Path.GetFileName(directory));
                }

                // 形如 .agents/skills/<技能名>.md
                foreach (var file in Directory.EnumerateFiles(root, "*.md").Order(StringComparer.OrdinalIgnoreCase))
                {
                    AddSkill(skills, seen, file, Describe(root), Path.GetFileNameWithoutExtension(file));
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "scan skills directory {Directory} failed", root);
            }
        }

        return [.. skills.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public string ReadContent(SkillInfo skill)
    {
        try
        {
            return File.Exists(skill.FilePath) ? ParseDocument(File.ReadAllText(skill.FilePath)).Body : string.Empty;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "read skill {Skill} failed", skill.Name);
            return string.Empty;
        }
    }

    /// <summary>技能目录在界面上的来源标签（相对应用目录）。</summary>
    private static string Describe(string root) =>
        Path.GetRelativePath(AppContext.BaseDirectory, root).Replace('\\', '/');

    /// <summary>取技能目录下的 SKILL.md（文件名忽略大小写）。</summary>
    private static string? FindSkillFile(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.md")
                .FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), "SKILL", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "enumerate skill directory {Directory} failed", directory);
            return null;
        }
    }

    private static void AddSkill(List<SkillInfo> skills, HashSet<string> seen, string filePath, string source, string? fallbackName)
    {
        try
        {
            var (name, description, _) = ParseDocument(File.ReadAllText(filePath));
            name = name?.Trim();
            if (string.IsNullOrEmpty(name)) name = fallbackName;
            if (string.IsNullOrEmpty(name)) return;

            if (!seen.Add(name))
            {
                Logger.Debug("duplicate skill {Skill} ignored ({File})", name, filePath);
                return;
            }

            skills.Add(new SkillInfo(name, description?.Trim() ?? string.Empty, source, filePath));
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "parse skill file {File} failed", filePath);
        }
    }

    /// <summary>拆分 YAML frontmatter（形如 --- name: xxx ---）与正文；没有 frontmatter 时整个文件都是正文。</summary>
    private static (string? Name, string? Description, string Body) ParseDocument(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) return (null, null, normalized.Trim());

        var end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return (null, null, normalized.Trim());

        var header = normalized[4..end].Split('\n');
        var bodyStart = normalized.IndexOf('\n', end + 1);
        var body = bodyStart < 0 ? string.Empty : normalized[(bodyStart + 1)..];

        string? name = null;
        string? description = null;
        for (var i = 0; i < header.Length; i++)
        {
            var line = header[i];
            var index = line.IndexOf(':');
            if (index <= 0 || char.IsWhiteSpace(line[0])) continue; // 只取顶层键

            var key = line[..index].Trim();
            var value = line[(index + 1)..].Trim();

            // 兼容 YAML 块标量（description: > 换行续写）
            if (value is ">" or "|" or ">-" or "|-")
            {
                var parts = new List<string>();
                while (i + 1 < header.Length && (header[i + 1].Length == 0 || char.IsWhiteSpace(header[i + 1][0])))
                {
                    parts.Add(header[++i].Trim());
                }
                value = string.Join(' ', parts.Where(p => p.Length > 0));
            }

            value = Unquote(value);
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) name = value;
            else if (key.Equals("description", StringComparison.OrdinalIgnoreCase)) description = value;
        }

        return (name, description, body.Trim());
    }

    private static string Unquote(string value)
    {
        if (value.Length < 2) return value;
        var first = value[0];
        return first is '"' or '\'' && value[^1] == first ? value[1..^1] : value;
    }
}
