// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Threading.Tasks;

namespace SoufflePet.Models;

/// <summary>工具调用的人工审批规则（保存在设置文件中）。</summary>
public enum ToolApprovalMode
{
    /// <summary>每次执行前都要用户点击允许。</summary>
    Manual,

    /// <summary>不再询问，直接执行。</summary>
    FullAccess
}

/// <summary>一次审批的最终结果。</summary>
public enum ToolApprovalOutcome
{
    Pending,
    Approved,
    Rejected,
    Canceled
}

/// <summary>
/// 一次待审批的工具调用：界面按钮调用 <see cref="Approve"/>/<see cref="Reject"/>，
/// 工具执行侧 await <see cref="Decision"/> 等待结果。
/// </summary>
public sealed class ToolApprovalRequest
{
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();

    public ToolApprovalRequest(string toolName, string arguments)
    {
        ToolName = toolName;
        Arguments = arguments;
    }

    public string ToolName { get; }

    /// <summary>展示给用户的调用参数（这里是完整的命令文本）。</summary>
    public string Arguments { get; }

    public ToolApprovalOutcome Outcome { get; private set; } = ToolApprovalOutcome.Pending;

    /// <summary>审批完成（含取消）时触发，用于刷新气泡上的状态。</summary>
    public event Action? Resolved;

    /// <summary>true 表示允许执行，false 表示拒绝或取消。</summary>
    public Task<bool> Decision => _completion.Task;

    public void Approve() => Resolve(approved: true);

    public void Reject() => Resolve(approved: false);

    /// <summary>对话被停止或请求超时：按拒绝处理，但不显示成「用户拒绝」。</summary>
    public void Cancel() => Resolve(approved: false, canceled: true);

    private void Resolve(bool approved, bool canceled = false)
    {
        lock (_gate)
        {
            if (Outcome != ToolApprovalOutcome.Pending) return;
            Outcome = canceled
                ? ToolApprovalOutcome.Canceled
                : approved
                    ? ToolApprovalOutcome.Approved
                    : ToolApprovalOutcome.Rejected;
            _completion.TrySetResult(approved);
        }

        Resolved?.Invoke();
    }
}