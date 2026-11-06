// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Threading;

namespace SoufflePet.Services;

/// <summary>
/// 当前正在执行的工具调用 id：随异步上下文流动，工具（文件编辑等）记录副作用时据此
/// 把结果贴回对应的工具气泡。与 <see cref="WorkspaceContext" /> 用法一致。
/// </summary>
internal static class ToolInvocationContext
{
    private static readonly AsyncLocal<string?> Holder = new();
    private static readonly AsyncLocal<string?> BackupDirectoryHolder = new();

    /// <summary>当前工具调用 id；不在工具调用内时为空字符串。</summary>
    public static string CurrentCallId => Holder.Value ?? string.Empty;

    /// <summary>当前会话的备份目录；编辑工具覆盖文件前把旧版本备份到这里。</summary>
    public static string? BackupDirectory => BackupDirectoryHolder.Value;

    /// <summary>在指定工具调用上下文中执行后续异步流程；释放返回值后恢复上一层设置。</summary>
    public static IDisposable Enter(string callId)
    {
        var previous = Holder.Value;
        Holder.Value = callId;
        return new Scope(previous);
    }

    /// <summary>设置编辑工具的备份目录（会话目录）；释放返回值后恢复。</summary>
    public static IDisposable EnterBackupDirectory(string? directory)
    {
        var previous = BackupDirectoryHolder.Value;
        BackupDirectoryHolder.Value = string.IsNullOrWhiteSpace(directory) ? null : directory;
        return new BackupScope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Holder.Value = previous;
        }
    }

    private sealed class BackupScope(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            BackupDirectoryHolder.Value = previous;
        }
    }
}