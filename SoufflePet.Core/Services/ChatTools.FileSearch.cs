// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.ComponentModel;
using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;

namespace SoufflePet.Services;

/// <summary>
/// 文件内容搜索与文件名查找，含 glob 匹配与目录枚举辅助。
/// </summary>
public sealed partial class ChatTools
{
    /// <summary>
    /// 在文件内容里查找关键词或正则：默认搜索整个会话工作区，可按 glob 过滤文件名。
    /// 只返回「文件:行号: 命中行」，并对条数、单文件大小和扫描文件数设上限，避免拖垮对话。
    /// </summary>
    private string SearchFiles(
        [Description("要查找的关键词；is_regex=true 时按 .NET 正则解释")]
        string query,
        [Description("搜索范围：目录或文件；相对路径按当前会话工作区解析，不传则搜索整个工作区")]
        string? path = null,
        [Description("只搜索文件名/相对路径匹配该通配符的文件，如 *.cs；不传则搜索所有文本文件")]
        string? glob = null,
        [Description("是否按正则表达式匹配（默认 false，按普通文本）")]
        bool isRegex = false,
        [Description("是否区分大小写（默认 false）")]
        bool caseSensitive = false,
        [Description("最多返回多少条匹配（默认 60，上限 300）")]
        int maxResults = SearchDefaultMaxResults)
    {
        query = query.Trim();
        if (query.Length == 0) return "查找关键词不能为空。";

        var rawPath = string.IsNullOrWhiteSpace(path) ? WorkspaceContext.Current : path.Trim();
        if (rawPath.StartsWith('@')) rawPath = rawPath[1..].Trim().Trim('"');

        string root;
        try
        {
            root = WorkspaceContext.Resolve(rawPath);
        }
        catch (Exception ex)
        {
            return $"搜索路径无效：{ex.Message}";
        }

        string? singleFile = null;
        if (Directory.Exists(root))
        {
            // 目录：递归搜索
        }
        else if (File.Exists(root))
        {
            singleFile = root;
        }
        else
        {
            return $"路径不存在：{root}";
        }

        return SearchText(root, singleFile, query, glob, isRegex, caseSensitive, maxResults);
    }

    /// <summary>
    /// 内容搜索核心：root 为目录时递归扫描，singleFile 非空时只搜该文件。
    /// 结果按「相对路径:行号: 命中行」输出，单行内容与总条数都做截断。
    /// </summary>
    internal static string SearchText(
        string root,
        string? singleFile,
        string query,
        string? glob,
        bool isRegex,
        bool caseSensitive,
        int maxResults)
    {
        var baseDirectory = singleFile is null ? root : Path.GetDirectoryName(singleFile) ?? root;
        var limit = Math.Clamp(maxResults, 1, SearchMaxResults);
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        Regex? regex = null;
        if (isRegex)
        {
            try
            {
                regex = new Regex(
                    query,
                    RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase),
                    TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException ex)
            {
                return $"正则表达式无效：{ex.Message}";
            }
        }

        var matcher = BuildGlobMatcher(glob, caseSensitive);
        var builder = new StringBuilder();
        var matches = 0;
        var scanned = 0;
        var resultCapHit = false;
        var fileCapHit = false;
        var regexTimedOut = false;

        IEnumerable<string> candidates = singleFile is null ? EnumerateSearchFiles(root) : [singleFile];
        foreach (var file in candidates)
        {
            if (scanned >= SearchMaxFiles)
            {
                fileCapHit = true;
                break;
            }

            scanned++;
            var info = new FileInfo(file);
            if (!info.Exists || info.Length == 0 || info.Length > SearchFileMaxBytes) continue;
            if (matcher is not null && !matcher(Path.GetRelativePath(baseDirectory, file))) continue;

            // 复用文本读取的编码/二进制校验：非 UTF-8/UTF-16 或含 NUL 的文件直接跳过
            if (!TryReadTextFile(file, out var text, out _, out _)) continue;

            var relative = Path.GetRelativePath(baseDirectory, file);
            var lines = text.Split('\n');
            // 文件以 \n 结尾时 Split 会多产出一个空段，去掉避免多余循环；
            // 不去掉中间合法空行以保证行号正确。
            if (lines.Length > 0 && lines[^1].Length == 0)
                lines = lines[..^1];
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].TrimEnd('\r');
                bool hit;
                if (regex is null)
                {
                    hit = line.Contains(query, comparison);
                }
                else
                {
                    try
                    {
                        hit = regex.IsMatch(line);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        regexTimedOut = true;
                        break;
                    }
                }

                if (!hit) continue;

                builder.Append('\n').Append(relative).Append(':').Append(index + 1).Append(": ")
                    .Append(line.Length <= 200 ? line : line[..200] + "…");
                if (++matches >= limit)
                {
                    resultCapHit = true;
                    break;
                }
            }

            if (resultCapHit || regexTimedOut) break;
        }

        if (matches == 0)
        {
            var empty = $"没有找到「{Truncate(query, 80)}」（扫描 {scanned} 个文件）";
            if (regexTimedOut) empty += "；正则匹配超时，请简化表达式。";
            return empty + (fileCapHit ? $"\n…（最多只扫描 {SearchMaxFiles} 个文件）" : string.Empty);
        }

        var report = new StringBuilder($"找到 {matches} 条匹配（扫描 {scanned} 个文件）：");
        report.Append(builder);
        if (resultCapHit) report.Append($"\n…（已达到 {limit} 条上限，可能还有更多匹配）");
        if (regexTimedOut) report.Append("\n…（正则匹配超时，结果可能不完整）");
        if (fileCapHit) report.Append($"\n…（已达到 {SearchMaxFiles} 个文件的扫描上限，结果可能不完整）");
        return report.ToString();
    }

    /// <summary>
    /// 按文件名/相对路径通配符查找文件：默认搜索整个会话工作区，只返回相对路径，不读文件内容。
    /// </summary>
    private string FindFiles(
        [Description("文件名或相对路径通配符，支持 * 与 ?，如 *.cs、src/**/*.tsx")]
        string pattern,
        [Description("搜索根目录：目录或文件；相对路径按当前会话工作区解析，不传则搜索整个工作区")]
        string? path = null,
        [Description("最多返回多少个文件（默认 100，上限 300）")]
        int maxResults = FindFilesDefaultMaxResults)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0) return "文件匹配模式不能为空。";

        var rawPath = string.IsNullOrWhiteSpace(path) ? WorkspaceContext.Current : path.Trim();
        if (rawPath.StartsWith('@')) rawPath = rawPath[1..].Trim().Trim('"');

        string root;
        try
        {
            root = WorkspaceContext.Resolve(rawPath);
        }
        catch (Exception ex)
        {
            return $"搜索路径无效：{ex.Message}";
        }

        string? singleFile = null;
        if (Directory.Exists(root))
        {
            // 目录：递归查找
        }
        else if (File.Exists(root))
        {
            singleFile = root;
        }
        else
        {
            return $"路径不存在：{root}";
        }

        return FindFilesIn(root, singleFile, pattern, maxResults);
    }

    /// <summary>文件名查找核心：root 为目录时递归遍历，singleFile 非空时只检查该文件。</summary>
    internal static string FindFilesIn(string root, string? singleFile, string pattern, int maxResults)
    {
        var matcher = BuildGlobMatcher(pattern, caseSensitive: false);
        if (matcher is null) return "文件匹配模式不能为空。";

        var baseDirectory = singleFile is null ? root : Path.GetDirectoryName(singleFile) ?? root;
        var limit = Math.Clamp(maxResults, 1, FindFilesMaxResults);
        var builder = new StringBuilder();
        var matches = 0;
        var scanned = 0;
        var resultCapHit = false;
        var fileCapHit = false;

        IEnumerable<string> candidates = singleFile is null ? EnumerateSearchFiles(root) : [singleFile];
        foreach (var file in candidates)
        {
            if (scanned >= SearchMaxFiles)
            {
                fileCapHit = true;
                break;
            }

            scanned++;
            var relative = Path.GetRelativePath(baseDirectory, file);
            if (!matcher(relative)) continue;

            builder.Append('\n').Append(relative);
            if (++matches >= limit)
            {
                resultCapHit = true;
                break;
            }
        }

        if (matches == 0)
        {
            var empty = $"没有找到匹配「{Truncate(pattern, 80)}」的文件（扫描 {scanned} 个文件）";
            return empty + (fileCapHit ? $"\n…（最多只扫描 {SearchMaxFiles} 个文件）" : string.Empty);
        }

        var report = new StringBuilder($"找到 {matches} 个文件（扫描 {scanned} 个文件）：");
        report.Append(builder);
        if (resultCapHit) report.Append($"\n…（已达到 {limit} 个文件上限，可能还有更多）");
        if (fileCapHit) report.Append($"\n…（已达到 {SearchMaxFiles} 个文件的扫描上限，结果可能不完整）");
        return report.ToString();
    }

    /// <summary>把用户给的 glob 转成匹配函数：不含分隔符时匹配文件名，否则匹配相对路径。</summary>
    private static Func<string, bool>? BuildGlobMatcher(string? glob, bool caseSensitive)
    {
        if (string.IsNullOrWhiteSpace(glob)) return null;

        var pattern = glob.Trim().Replace('\\', '/');
        if (pattern.StartsWith("./", StringComparison.Ordinal)) pattern = pattern[2..];
        var matchPath = pattern.Contains('/');

        return relativePath =>
        {
            var normalized = relativePath.Replace('\\', '/');
            var candidate = matchPath ? normalized : Path.GetFileName(normalized);
            return FileSystemName.MatchesSimpleExpression(pattern, candidate, ignoreCase: !caseSensitive);
        };
    }

    /// <summary>深度优先枚举文件，跳过版本库/构建目录与重解析点，读目录失败时自动跳过。</summary>
    private static IEnumerable<string> EnumerateSearchFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                bool isDirectory;
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Hidden) != 0) continue;
                    isDirectory = (attributes & FileAttributes.Directory) != 0;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 单个条目不可访问：跳过，不影响其余结果
                    continue;
                }

                if (isDirectory)
                {
                    if (!SearchSkippedDirectories.Contains(Path.GetFileName(entry))) pending.Push(entry);
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }
}
