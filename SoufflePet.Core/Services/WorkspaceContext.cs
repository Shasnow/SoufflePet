// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.IO;
using System.Threading;

namespace SoufflePet.Services;

/// <summary>
/// 会话的逻辑工作目录：决定相对路径解析、AGENTS.md 与技能目录查找、命令执行的实际目录。
/// 不改变进程物理 cwd —— 进程 cwd 是全局状态，两个会话并发生成时，后开的会话会把前一个会话
/// 的工具调用、系统提示词全部带偏。
///
/// 用法：每轮对话在异步流程入口 <see cref="Enter"/> 一次，值随异步上下文流动，
/// 不同会话的对话流程各自持有自己的值，互不影响。
/// </summary>
public static class WorkspaceContext
{
    private static readonly AsyncLocal<string?> Holder = new();

    /// <summary>进程启动时的目录，等同于旧行为里「没有工作区」时的 cwd。</summary>
    public static string Fallback { get; } = AppContext.BaseDirectory;

    /// <summary>
    /// 当前异步流程所属会话的工作区；对话轮次之外（设置页、添加附件等）没有会话上下文，回落到应用目录。
    /// </summary>
    public static string Current => Holder.Value is { Length: > 0 } path ? path : Fallback;

    /// <summary>在指定工作区内执行后续异步流程；释放返回值后恢复上一层设置。</summary>
    public static IDisposable Enter(string? workspacePath)
    {
        var previous = Holder.Value;
        Holder.Value = string.IsNullOrEmpty(workspacePath) ? null : workspacePath;
        return new Scope(previous);
    }

    /// <summary>按会话工作区解析相对路径（绝对路径原样规范化）；显式传入基准目录时优先用它。</summary>
    public static string Resolve(string path, string? basePath = null) =>
        Path.GetFullPath(path, string.IsNullOrEmpty(basePath) ? Current : basePath);

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
}
