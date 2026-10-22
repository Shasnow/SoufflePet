// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Serilog;
using SoufflePet.Models;

namespace SoufflePet.Services;

/// <summary>一个技能：应用目录或家目录下 <c>.agents/skills</c>、<c>.souffle/skills</c> 中的说明文档。</summary>
public sealed record SkillInfo(string Name, string Description, string Source, string FilePath)
{
    public bool HasDescription => Description.Length > 0;
}

/// <summary>安装结果：Message 可直接展示给用户，SkillName 为装好后选中的技能名（失败时为 null）。</summary>
public readonly record struct SkillInstallResult(bool Success, string Message, string? SkillName = null);

/// <summary>扫描并读取技能文档。技能本身无需注册，扫描目录即可发现。</summary>
public interface ISkillService
{
    /// <summary>扫描根目录：应用目录下的 <c>.agents/skills</c>、<c>.souffle/skills</c>，以及家目录下的 <c>.agents/skills</c>。</summary>
    IReadOnlyList<string> SearchDirectories { get; }

    /// <summary>目录在界面上展示用的标签：应用目录内给相对路径，家目录等外部目录用 <c>~/</c> 开头。</summary>
    string DescribeDirectory(string directory);

    /// <summary>列出全部技能（同名只保留先扫描到的）。目录不存在或文件解析失败只记日志。</summary>
    IReadOnlyList<SkillInfo> Discover();

    /// <summary>列出已启用的技能：<see cref="SouffleSettings.DisabledSkills"/> 中的名字忽略大小写。</summary>
    IReadOnlyList<SkillInfo> DiscoverEnabled();

    /// <summary>技能是否启用（停用列表按名称、忽略大小写匹配）。</summary>
    bool IsEnabled(SkillInfo skill);

    /// <summary>
    /// 每轮对话注入系统提示词的技能清单（仅 name 与 description 两项 frontmatter），
    /// 让模型知道有哪些技能可用，再按需调用 read_skill 读取正文。没有已启用技能时返回空串。
    /// </summary>
    string BuildCatalog();

    /// <summary>读取技能正文（去掉 YAML frontmatter）；文件不存在时返回空字符串。</summary>
    string ReadContent(SkillInfo skill);

    /// <summary>
    /// 从压缩包安装技能：包内需包含 SKILL.md（可位于一级子目录中），
    /// 解压到技能目录下以技能名命名的子目录，同名目录会被覆盖。返回安装结果。
    /// </summary>
    SkillInstallResult Install(string archivePath);
}

/// <summary>
/// 技能目录约定：每个技能占一个子目录，内含带 YAML frontmatter（name/description）的 SKILL.md；
/// 也支持直接放在技能目录下的单个 *.md 文件。正文即 SKILL.md 去掉 frontmatter 后的内容。
/// </summary>
public sealed class SkillService(SouffleSettings settings) : ISkillService
{
    private static readonly ILogger Logger = Log.ForContext<SkillService>();

    public IReadOnlyList<string> SearchDirectories { get; } =
    [
        Path.Combine(AppContext.BaseDirectory, ".agents", "skills"),
        Path.Combine(AppContext.BaseDirectory, ".souffle", "skills"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "skills")
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
                    AddSkill(skills, seen, entry, DescribeDirectory(root), Path.GetFileName(directory));
                }

                // 形如 .agents/skills/<技能名>.md
                foreach (var file in Directory.EnumerateFiles(root, "*.md").Order(StringComparer.OrdinalIgnoreCase))
                {
                    AddSkill(skills, seen, file, DescribeDirectory(root), Path.GetFileNameWithoutExtension(file));
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "scan skills directory {Directory} failed", root);
            }
        }

        return [.. skills.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public IReadOnlyList<SkillInfo> DiscoverEnabled() => [.. Discover().Where(IsEnabled)];

    public bool IsEnabled(SkillInfo skill) =>
        !settings.DisabledSkills.Any(d => string.Equals(d, skill.Name, StringComparison.OrdinalIgnoreCase));

    public string BuildCatalog()
    {
        var skills = DiscoverEnabled();
        if (skills.Count == 0) return string.Empty;

        var builder = new StringBuilder("可用技能（任务与某个技能相关时，先调用 read_skill 读取完整说明，再按说明执行）：");
        foreach (var skill in skills)
        {
            builder.Append("\n- ").Append(skill.Name);
            if (skill.HasDescription) builder.Append("：").Append(skill.Description);
        }

        return builder.ToString();
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

    public SkillInstallResult Install(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            return new SkillInstallResult(false, "找不到压缩包");

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var entries = archive.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name) && !IsJunk(e.FullName))
                .ToList();

            // 取层级最浅的 SKILL.md 作为技能入口，它所在的目录就是技能目录
            var entry = entries
                .Where(e => string.Equals(e.Name, "SKILL.md", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName.Count(c => c == '/'))
                .FirstOrDefault();
            if (entry is null) return new SkillInstallResult(false, "压缩包里没有找到 SKILL.md");

            var prefix = entry.FullName[..^entry.Name.Length]; // 形如 "my-skill/"，根目录时为空串
            var folder = ResolveFolderName(entry, prefix, archivePath);

            var target = Path.Combine(SearchDirectories[0], folder);
            var existed = Directory.Exists(target);
            Directory.CreateDirectory(target);

            var targetRoot = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
            foreach (var item in entries)
            {
                if (!item.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                var relative = item.FullName[prefix.Length..];
                if (relative.Length == 0) continue;

                var destination = Path.GetFullPath(
                    Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar)));

                // 防目录穿越：解压结果必须仍落在技能目录内
                if (!destination.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                item.ExtractToFile(destination, overwrite: true);
            }

            Logger.Information("skill installed {Skill} from {Archive}", folder, archivePath);
            return new SkillInstallResult(
                true,
                existed ? $"已更新技能「{folder}」" : $"已安装技能「{folder}」",
                folder);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "install skill from {Archive} failed", archivePath);
            return new SkillInstallResult(false, $"安装失败：{ex.Message}");
        }
    }

    /// <summary>装进哪个文件夹：优先用 SKILL.md 里声明的 name，退回到包内目录名，再退回到压缩包文件名。</summary>
    private static string ResolveFolderName(ZipArchiveEntry entry, string prefix, string archivePath)
    {
        var candidates = new List<string?>();

        try
        {
            using var reader = new StreamReader(entry.Open());
            candidates.Add(ParseDocument(reader.ReadToEnd()).Name);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "read skill frontmatter from {Entry} failed", entry.FullName);
        }

        candidates.Add(prefix.Length > 0 ? prefix.TrimEnd('/') : null);
        candidates.Add(Path.GetFileNameWithoutExtension(archivePath));

        foreach (var candidate in candidates)
        {
            var name = Sanitize(candidate);
            if (name.Length > 0) return name;
        }

        return "skill";
    }

    /// <summary>换成合法的目录名：不允许的字符换成连字符；多个候选都为空时由调用方继续尝试下一个。</summary>
    private static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Trim().Length);
        foreach (var ch in name.Trim())
            builder.Append(invalid.Contains(ch) ? '-' : ch);

        var result = builder.ToString().Trim(' ', '.', '-');
        return result is "" or "." or ".." ? string.Empty : result;
    }

    /// <summary>压缩包里的系统垃圾（__MACOSX、.DS_Store 等）不参与安装。</summary>
    private static bool IsJunk(string path) =>
        path.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)
        || path.Split('/').Any(part => part.StartsWith('.'));

    /// <summary>技能目录在界面上的标签：应用目录内用相对路径，家目录等外部目录用 ~ 开头。</summary>
    public string DescribeDirectory(string directory)
    {
        var relative = Path.GetRelativePath(AppContext.BaseDirectory, directory);

        // 不同盘符时 GetRelativePath 会直接返回绝对路径，这种情况交给下面的家目录分支处理
        if (!Path.IsPathRooted(relative) && !relative.StartsWith("..", StringComparison.Ordinal))
            return relative.Replace('\\', '/');

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var fromHome = Path.GetRelativePath(home, directory);
        return !Path.IsPathRooted(fromHome) && !fromHome.StartsWith("..", StringComparison.Ordinal)
            ? $"~/{fromHome.Replace('\\', '/')}"
            : directory.Replace('\\', '/');
    }

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
