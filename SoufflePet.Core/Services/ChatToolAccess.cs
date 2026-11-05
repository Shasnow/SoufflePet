// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Threading;
using System.Threading.Tasks;
using SoufflePet.Models;

namespace SoufflePet.Services;

/// <summary>
/// 工具审批规则：保存在设置文件（<see cref="SouffleSettings.ToolApproval"/>）里，修改后立即生效。
/// 手动审批时通过 <see cref="ApprovalRequested"/> 交给对话界面弹出确认，工具执行侧等待结果。
/// </summary>
public sealed class ChatToolAccess
{
    private readonly SouffleSettings _settings;

    public ChatToolAccess(SouffleSettings settings) => _settings = settings;

    /// <summary>当前审批规则，默认每次询问。</summary>
    public ToolApprovalMode ApprovalMode
    {
        get => _settings.ToolApproval;
        set => _settings.ToolApproval = value;
    }

    /// <summary>需要用户确认时触发；没有订阅者（界面不可用）时视为拒绝。</summary>
    public event Action<ToolApprovalRequest>? ApprovalRequested;

    /// <summary>为一次命令执行申请许可：完全访问直接放行，手动审批等待用户点击允许。</summary>
    public Task<bool> RequestShellApprovalAsync(string command, CancellationToken cancellationToken)
        => RequestApprovalAsync("run_shell_command", command, cancellationToken);

    /// <summary>为任意有副作用的工具申请许可（例如向平台发送文件）。</summary>
    public async Task<bool> RequestApprovalAsync(
        string toolName,
        string detail,
        CancellationToken cancellationToken)
    {
        if (ApprovalMode == ToolApprovalMode.FullAccess) return true;

        var request = new ToolApprovalRequest(toolName, detail);
        if (ApprovalRequested is { } handler) handler(request);
        else request.Reject();

        // 停止对话时同步取消等待，避免工具循环卡住
        using var registration = cancellationToken.Register(request.Cancel);
        return await request.Decision.ConfigureAwait(false);
    }
}
