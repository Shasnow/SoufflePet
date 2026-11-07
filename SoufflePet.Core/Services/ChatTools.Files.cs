// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using SoufflePet.Core.Chat;

namespace SoufflePet.Services;

/// <summary>
/// 文件展示、写入、编辑及其编码识别、原子写回、备份、差异记录与路径解析等辅助。
/// </summary>
public sealed partial class ChatTools
{
    /// <summary>在本地对话窗口展示图片或文件卡片。</summary>
    private string ShowFile(
        [Description("本地文件的绝对路径")]
        string path,
        [Description("展示文件名")]
        string? displayName = null)
    {
        if (!TryResolveLocalFile(path, out var fullPath, out var info, out var error)) return error;
        if (info.Length > ShowFileMaxBytes)
            return $"文件「{info.Name}」大小为 {FormatSize(info.Length)}，超过 {FormatSize(ShowFileMaxBytes)} 的展示上限。";

        var name = string.IsNullOrWhiteSpace(displayName) ? info.Name : displayName.Trim();
        var isImage = ImageExtensions.Contains(info.Extension);
        _files.Show(new ChatFileDisplay(fullPath, name, isImage));

        return isImage
            ? $"已在对话窗口展示图片「{name}」。"
            : $"已在对话窗口展示文件「{name}」。";
    }

    /// <summary>
    /// 写入文本文件：新建或整体覆盖。写入前计算行级差异并请求审批，
    /// 成功后把差异交给界面展示；内容没变化时不写盘。
    /// </summary>
    private async Task<string> WriteFile(
        [Description("文件路径：绝对路径，或相对当前会话工作区的路径")]
        string path,
        [Description("要写入的完整文本内容")]
        string content,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveWritableFile(path, out var fullPath, out var error)) return error;
        if (content.Length > MaxEditChars)
            return $"写入内容过长（{content.Length} 字符），超过 {MaxEditChars} 字符的上限。";

        string? oldText = null;
        Encoding encoding = Utf8NoBom;
        if (File.Exists(fullPath))
        {
            if (!TryReadTextFile(fullPath, out var existing, out var existingEncoding, out var readError))
                return readError;
            oldText = existing;
            encoding = existingEncoding;
        }

        if (oldText == content)
            return $"文件「{fullPath}」内容没有变化，未写入。";

        var diff = LineDiff.Compute(fullPath, oldText ?? string.Empty, content);

        // 覆盖已有文件时把旧版本留在会话备份目录，写坏/写错还能找回
        var backupPath = oldText is null ? null : BackupPathFor(fullPath);
        string? actualBackup;
        try
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            // 临时文件 + File.Replace：中途崩溃不会把原文件截断；沿用原编码与 BOM
            actualBackup = WriteTextFile(fullPath, content, encoding, backupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            return $"写入文件失败：{ex.Message}";
        }

        RecordEdit(diff);
        var result = EditResult("已写入", fullPath, diff);
        if (actualBackup is not null) result += $"\n原文件已备份到 {actualBackup}。";
        return result;
    }

    /// <summary>
    /// 编辑文本文件：把一段旧文本替换成新文本。默认只允许唯一匹配，
    /// 多处匹配时必须显式 replace_all，避免误改。
    /// </summary>
    private async Task<string> EditFile(
        [Description("文件路径：绝对路径，或相对当前会话工作区的路径")]
        string path,
        [Description("要替换的旧文本；匹配时忽略换行符差异")]
        string oldString,
        [Description("替换后的新文本")]
        string newString,
        [Description("是否替换全部匹配（默认 false；存在多个匹配时必须显式开启）")]
        bool replaceAll = false,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveWritableFile(path, out var fullPath, out var error)) return error;
        if (!File.Exists(fullPath)) return $"文件不存在：{fullPath}";
        if (!TryReadTextFile(fullPath, out var text, out var encoding, out var readError)) return readError;

        if (oldString.Length == 0) return "old_string 不能为空；要整体覆盖请用 write_file。";
        if (NormalizeNewlines(oldString) == NormalizeNewlines(newString))
            return "新旧文本相同，未做修改。";

        var count = ReplaceIgnoringLineEndings(text, oldString, newString, replaceAll, out var updated);
        if (count == 0)
            return $"文件里没有找到要替换的内容，请先用 read_file 核对。old_string 开头：{Truncate(oldString, 120)}";
        if (count > 1 && !replaceAll)
            return $"old_string 在文件里出现了 {count} 次，请提供更长的上下文使其唯一，或传 replace_all=true。";

        if (updated == text) return "替换后内容没有变化，未写入。";
        if (updated.Length > MaxEditChars)
            return $"修改后的内容超过 {MaxEditChars} 字符的上限，未写入。";

        var diff = LineDiff.Compute(fullPath, text, updated);

        var backupPath = BackupPathFor(fullPath);
        string? actualBackup;
        try
        {
            actualBackup = WriteTextFile(fullPath, updated, encoding, backupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            return $"写入文件失败：{ex.Message}";
        }

        RecordEdit(diff);
        var result = EditResult("已编辑", fullPath, diff);
        if (actualBackup is not null) result += $"\n原文件已备份到 {actualBackup}。";
        return result;
    }

    /// <summary>工具回给模型的写盘结果：有差异时带增删行数，过大时说明未生成 diff。</summary>
    private static string EditResult(string verb, string fullPath, ChatFileDiffState diff) =>
        diff.Truncated && diff.Hunks.Count == 0
            ? $"{verb} {fullPath}（文件过大，未生成 diff）。"
            : $"{verb} {fullPath}（+{diff.Added} −{diff.Removed}）。";

    /// <summary>把本次编辑的差异交给界面；没有界面订阅者时静默丢弃。</summary>
    private void RecordEdit(ChatFileDiffState diff)
    {
        var callId = ToolInvocationContext.CurrentCallId;
        if (callId.Length == 0) return;
        // 文件过大时也把 truncated 状态送出去，界面显示「未生成 diff」而不是什么都不显示
        if (diff.Hunks.Count == 0 && !diff.Truncated) return;
        _files.RecordEdit(new ChatFileEdit(callId, [diff]));
    }

    /// <summary>解析可写文件路径：去掉 @ 前缀，拒绝目录；文件可以不存在（新建）。</summary>
    private static bool TryResolveWritableFile(string path, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        error = string.Empty;

        var raw = path.Trim();
        if (raw.StartsWith('@')) raw = raw[1..].Trim().Trim('"');
        if (raw.Length == 0)
        {
            error = "文件路径不能为空。";
            return false;
        }

        try
        {
            fullPath = WorkspaceContext.Resolve(raw);
        }
        catch (Exception ex)
        {
            error = $"文件路径无效：{ex.Message}";
            return false;
        }

        if (Directory.Exists(fullPath))
        {
            error = $"「{fullPath}」是目录，不能作为文件写入。";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 读取文本文件并带回原编码：支持 UTF-8（有无 BOM）、UTF-16、UTF-32；
    /// 写回时沿用该编码，避免只改一行却把整个文件换成 UTF-8 无 BOM。
    /// </summary>
    internal static bool TryReadTextFile(
        string fullPath,
        out string text,
        out Encoding encoding,
        out string error)
    {
        text = string.Empty;
        encoding = Utf8NoBom;
        error = string.Empty;

        try
        {
            var info = new FileInfo(fullPath);
            if (info.Length > MaxEditableBytes)
            {
                error = $"文件「{info.Name}」大小为 {FormatSize(info.Length)}，"
                    + $"超过可编辑上限 {FormatSize(MaxEditableBytes)}。";
                return false;
            }

            return TryDecodeText(File.ReadAllBytes(info.FullName), out text, out encoding, out error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"读取文件失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 原子写回文本文件：先写同目录临时文件，再用 <see cref="File.Replace(string, string, string?, bool)" />
    /// 替换目标。中途失败或断电时原文件保持完整，不会出现写到一半被截断。
    /// 覆盖已有文件时可在替换前把旧版本复制到 <paramref name="backupPath" />（可跨盘），返回实际备份路径。
    /// </summary>
    internal static string? WriteTextFile(
        string fullPath,
        string text,
        Encoding encoding,
        string? backupPath = null)
    {
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory)) directory = ".";

        var tempPath = Path.Combine(directory, $".souffle-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, text, encoding);

            if (!File.Exists(fullPath))
            {
                File.Move(tempPath, fullPath);
                return null;
            }

            // 备份先复制到会话目录（跨盘也能用），再原子替换目标。
            // File.Replace 的备份参数要求与目标同卷，所以这里不把会话备份直接交给它。
            if (!string.IsNullOrEmpty(backupPath)) SaveBackup(fullPath, backupPath);

            File.Replace(tempPath, fullPath, null, ignoreMetadataErrors: true);
            return string.IsNullOrEmpty(backupPath) ? null : backupPath;
        }
        finally
        {
            DeleteIfExists(tempPath);
        }
    }

    /// <summary>把旧版本完整复制到备份路径：先写同目录临时文件再改名，避免留下半份备份。</summary>
    private static void SaveBackup(string source, string backupPath)
    {
        var directory = Path.GetDirectoryName(backupPath);
        if (string.IsNullOrEmpty(directory)) directory = ".";

        Directory.CreateDirectory(directory);
        var tempBackup = Path.Combine(directory, $".souffle-{Guid.NewGuid():N}.bak.tmp");
        try
        {
            File.Copy(source, tempBackup, overwrite: false);
            File.Move(tempBackup, backupPath, overwrite: true);
        }
        finally
        {
            DeleteIfExists(tempBackup);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (!File.Exists(path)) return;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Debug(ex, "delete edit temp file failed path={Path}", path);
        }
    }

    /// <summary>同一文件的备份路径稳定：会话目录 <c>backups/</c> 下保留最近一个版本。</summary>
    internal static string? BackupPathFor(string fullPath)
    {
        var directory = ToolInvocationContext.BackupDirectory;
        if (string.IsNullOrWhiteSpace(directory)) return null;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant())))[..16];
        var name = Path.GetFileName(fullPath).ToLowerInvariant();
        if (name.Length > 60) name = name[..60];
        return Path.Combine(directory, "backups", $"{name}.{hash}.bak");
    }

    /// <summary>按 BOM 识别编码并严格解码；无 BOM 时只接受 UTF-8。</summary>
    internal static bool TryDecodeText(
        byte[] bytes,
        out string text,
        out Encoding encoding,
        out string error)
    {
        text = string.Empty;
        error = string.Empty;
        var offset = 0;

        if (HasPrefix(bytes, [0xEF, 0xBB, 0xBF]))
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);
            offset = 3;
        }
        else if (HasPrefix(bytes, [0xFF, 0xFE, 0x00, 0x00]))
        {
            encoding = new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true);
            offset = 4;
        }
        else if (HasPrefix(bytes, [0x00, 0x00, 0xFE, 0xFF]))
        {
            encoding = new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: true);
            offset = 4;
        }
        else if (HasPrefix(bytes, [0xFF, 0xFE]))
        {
            encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true);
            offset = 2;
        }
        else if (HasPrefix(bytes, [0xFE, 0xFF]))
        {
            encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true);
            offset = 2;
        }
        else
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        }

        try
        {
            text = encoding.GetString(bytes, offset, bytes.Length - offset);
        }
        catch (Exception ex) when (ex is DecoderFallbackException or ArgumentException)
        {
            error = "文件不是 UTF-8 / UTF-16 文本，无法编辑。";
            return false;
        }

        if (text.Contains('\0'))
        {
            error = "文件包含 NUL 字节，不是文本文件，无法编辑。";
            return false;
        }

        return true;
    }

    private static bool HasPrefix(byte[] bytes, ReadOnlySpan<byte> prefix) =>
        bytes.AsSpan().StartsWith(prefix);

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0
            ? text
            : string.Concat(text.AsSpan(0, index), newValue, text.AsSpan(index + oldValue.Length));
    }

    /// <summary>
    /// 忽略换行符差异的替换：匹配前把 CRLF/LF/CR 统一成 LF，写回时再还原成原文件的换行风格。
    /// 模型给出的 old_string 通常是 LF，而 Windows 文件多为 CRLF，直接精确匹配会找不到。
    /// </summary>
    internal static int ReplaceIgnoringLineEndings(
        string text,
        string oldString,
        string newString,
        bool replaceAll,
        out string updated)
    {
        var lineEnding = DetectLineEnding(text);
        var source = NormalizeNewlines(text);
        var normalizedOld = NormalizeNewlines(oldString);
        var normalizedNew = NormalizeNewlines(newString);

        var count = CountOccurrences(source, normalizedOld);
        updated = text;
        if (count == 0) return 0;

        var replaced = replaceAll
            ? source.Replace(normalizedOld, normalizedNew, StringComparison.Ordinal)
            : ReplaceFirst(source, normalizedOld, normalizedNew);
        updated = RestoreLineEndings(replaced, lineEnding);
        return count;
    }

    /// <summary>统一成 LF，便于跨平台比较与替换。</summary>
    internal static string NormalizeNewlines(string value) =>
        value.IndexOf('\r') < 0
            ? value
            : value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>取文件第一个换行符的风格，作为写回时统一的换行符。</summary>
    private static string DetectLineEnding(string text)
    {
        var lf = text.IndexOf('\n');
        if (lf >= 0) return lf > 0 && text[lf - 1] == '\r' ? "\r\n" : "\n";
        return text.IndexOf('\r') >= 0 ? "\r" : "\n";
    }

    private static string RestoreLineEndings(string value, string lineEnding) =>
        lineEnding == "\n" ? value : value.Replace("\n", lineEnding, StringComparison.Ordinal);

    /// <summary>解析并校验本地文件路径：去掉 @ 前缀，拒绝目录与不存在的文件。</summary>
    private static bool TryResolveLocalFile(
        string path,
        out string fullPath,
        out FileInfo info,
        out string error)
    {
        fullPath = string.Empty;
        info = null!;
        error = string.Empty;

        var raw = path.Trim();
        if (raw.StartsWith('@')) raw = raw[1..].Trim().Trim('"');
        if (raw.Length == 0)
        {
            error = "文件路径不能为空。";
            return false;
        }

        try
        {
            // 相对路径按当前会话的逻辑工作区解析，不能依赖进程 cwd
            fullPath = WorkspaceContext.Resolve(raw);
        }
        catch (Exception ex)
        {
            error = $"文件路径无效：{ex.Message}";
            return false;
        }

        if (Directory.Exists(fullPath))
        {
            error = $"「{fullPath}」是目录，不能作为文件使用。";
            return false;
        }

        info = new FileInfo(fullPath);
        if (info.Exists) return true;

        error = $"文件不存在：{fullPath}";
        return false;
    }
}
