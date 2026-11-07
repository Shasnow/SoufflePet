// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.IO;
using System.Text;
using SoufflePet.Services;
using Xunit;

namespace SoufflePet.Core.Tests;

/// <summary>编辑工具的文本读取：编码/BOM 必须保留，非 UTF 文本与二进制要拒绝。</summary>
public sealed class ChatToolsTextFileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "soufflepet-text-tests", Guid.NewGuid().ToString("N"));

    public ChatToolsTextFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 测试目录偶尔被索引器占用：让系统自己清理
        }
    }

    [Fact]
    public void Utf8BomIsPreservedWhenWritingBack()
    {
        var path = Path.Combine(_root, "utf8-bom.txt");
        var payload = Encoding.UTF8.GetBytes("你好\r\n第二行");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. payload]);

        Assert.True(ChatTools.TryReadTextFile(path, out var text, out var encoding, out var error));
        Assert.Equal(string.Empty, error);
        Assert.Equal("你好\r\n第二行", text);
        Assert.True(HasUtf8Bom(encoding.GetPreamble()));

        ChatTools.WriteTextFile(path, "改过的内容", encoding);
        var bytes = File.ReadAllBytes(path);
        Assert.True(HasUtf8Bom(bytes));
        Assert.Equal("改过的内容", Encoding.UTF8.GetString(bytes.AsSpan(3)));
    }

    [Fact]
    public void Utf16BomIsPreservedWhenWritingBack()
    {
        var path = Path.Combine(_root, "utf16.txt");
        File.WriteAllText(path, "a\r\nb", Encoding.Unicode);

        Assert.True(ChatTools.TryReadTextFile(path, out var text, out var encoding, out var error));
        Assert.Equal(string.Empty, error);
        Assert.Equal("a\r\nb", text);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, encoding.GetPreamble());

        ChatTools.WriteTextFile(path, "改", encoding);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal((byte)0xFF, bytes[0]);
        Assert.Equal((byte)0xFE, bytes[1]);
        Assert.Equal("改", Encoding.Unicode.GetString(bytes.AsSpan(2)));
    }

    [Fact]
    public void Utf8WithoutBomStaysWithoutBom()
    {
        var path = Path.Combine(_root, "utf8-no-bom.txt");
        File.WriteAllText(path, "abc", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Assert.True(ChatTools.TryReadTextFile(path, out var text, out var encoding, out var error));
        Assert.Equal(string.Empty, error);
        Assert.Equal("abc", text);
        Assert.Empty(encoding.GetPreamble());

        ChatTools.WriteTextFile(path, "改", encoding);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal("改", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void InvalidUtf8IsRejected()
    {
        var path = Path.Combine(_root, "gbk.txt");
        File.WriteAllBytes(path, [0x41, 0x80, 0x42]);

        Assert.False(ChatTools.TryReadTextFile(path, out _, out _, out var error));
        Assert.Contains("UTF-8", error);
    }

    [Fact]
    public void AtomicWriteReplacesTargetAndKeepsPreviousVersion()
    {
        var path = Path.Combine(_root, "atomic.txt");
        var backup = Path.Combine(_root, "backups", "atomic.txt.bak");
        File.WriteAllText(path, "old", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Assert.Equal(backup, ChatTools.WriteTextFile(path, "new", new UTF8Encoding(false), backup));

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal("old", File.ReadAllText(backup));

        // 同一文件的备份只保留上一版
        ChatTools.WriteTextFile(path, "newer", new UTF8Encoding(false), backup);
        Assert.Equal("new", File.ReadAllText(backup));
    }

    [Fact]
    public void AtomicWriteCreatesNewFileWithoutBackup()
    {
        var path = Path.Combine(_root, "brand-new.txt");
        var backup = Path.Combine(_root, "backups", "brand-new.txt.bak");

        Assert.Null(ChatTools.WriteTextFile(path, "new", new UTF8Encoding(false), backup));

        Assert.True(File.Exists(path));
        Assert.Equal("new", File.ReadAllText(path));
        Assert.False(File.Exists(backup));
    }

    [Fact]
    public void BackupPathIsStableAndInsideSessionBackupsDirectory()
    {
        using var scope = ToolInvocationContext.EnterBackupDirectory(_root);

        var path = Path.Combine(_root, "notes.txt");
        var backup = ChatTools.BackupPathFor(path);
        Assert.NotNull(backup);
        Assert.Equal(Path.Combine(_root, "backups"), Path.GetDirectoryName(backup));
        Assert.StartsWith("notes.txt.", Path.GetFileName(backup));
        Assert.EndsWith(".bak", backup);

        // 大小写不同仍是同一路径，备份不会重复堆积
        Assert.Equal(backup, ChatTools.BackupPathFor(path.ToUpperInvariant()));
    }

    [Fact]
    public void NoBackupPathWithoutSessionContext()
    {
        Assert.Null(ToolInvocationContext.BackupDirectory);
        Assert.Null(ChatTools.BackupPathFor(Path.Combine(_root, "notes.txt")));
    }

    [Fact]
    public void BinaryFileIsRejected()
    {
        var path = Path.Combine(_root, "binary.dat");
        File.WriteAllBytes(path, [0x41, 0x00, 0x42]);

        Assert.False(ChatTools.TryReadTextFile(path, out _, out _, out var error));
        Assert.Contains("NUL", error);
    }

    [Fact]
    public void CrlfFileMatchesLfOldStringAndKeepsCrlf()
    {
        var text = "第一行\r\n第二行\r\n第三行";

        var count = ChatTools.ReplaceIgnoringLineEndings(
            text, "第二行\n第三行", "第二行\n新第三行", false, out var updated);

        Assert.Equal(1, count);
        Assert.Equal("第一行\r\n第二行\r\n新第三行", updated);
    }

    [Fact]
    public void LfFileMatchesCrlfOldStringAndKeepsLf()
    {
        var text = "a\nb\nc";

        var count = ChatTools.ReplaceIgnoringLineEndings(
            text, "a\r\nb", "a\r\nx", false, out var updated);

        Assert.Equal(1, count);
        Assert.Equal("a\nx\nc", updated);
    }

    [Fact]
    public void ReplaceAllIgnoresLineEndingsAndKeepsOriginalStyle()
    {
        var text = "x\r\ny\r\nx\r\ny";

        var count = ChatTools.ReplaceIgnoringLineEndings(
            text, "x\ny", "z", true, out var updated);

        Assert.Equal(2, count);
        Assert.Equal("z\r\nz", updated);
    }

    private static bool HasUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
}