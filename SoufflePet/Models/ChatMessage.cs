// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SoufflePet.Models;

/// <summary>
/// 一条对话消息：IsUser 决定气泡的左右对齐与配色；IsTool 表示工具调用记录（不进入请求历史）；
/// IsReasoning 表示模型的思考过程（同样不进入请求历史）；
/// Text 支持流式追加时刷新；带 Approval 的消息表示等待用户确认的工具调用，气泡上显示允许/拒绝按钮。
/// </summary>
public sealed partial class ChatMessage : ObservableObject
{
    /// <summary>审批结果回写时以原始文本为底，避免状态覆盖掉命令内容。</summary>
    private readonly string _baseText;

    public ChatMessage(string text, bool isUser, bool isTool = false, bool isReasoning = false,
        ToolApprovalRequest? approval = null)
    {
        Text = text;
        _baseText = text;
        IsUser = isUser;
        IsTool = isTool;
        IsReasoning = isReasoning;
        Approval = approval;

        if (approval is null) return;

        // 请求可能在气泡插入前就已被取消（例如立刻按了停止）
        approval.Resolved += OnApprovalResolved;
        if (approval.Outcome == ToolApprovalOutcome.Pending) IsPending = true;
        else OnApprovalResolved();
    }

    [ObservableProperty] public partial string Text { get; set; }

    /// <summary>本轮回复的 token 用量（结束后显示在气泡底部）；空串表示不显示。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUsage))]
    public partial string UsageText { get; set; } = string.Empty;

    public bool HasUsage => UsageText.Length > 0;

    public bool IsUser { get; }

    /// <summary>工具调用记录（弱化样式展示，不参与上下文）。</summary>
    public bool IsTool { get; }

    /// <summary>模型的思考过程（弱化样式展示，不参与上下文）。</summary>
    public bool IsReasoning { get; }

    /// <summary>需要用户审批的工具调用；为 null 表示普通消息。</summary>
    public ToolApprovalRequest? Approval { get; }

    public bool IsApproval => Approval is not null;

    /// <summary>是否仍在等待用户点击允许/拒绝。</summary>
    [ObservableProperty]
    public partial bool IsPending { get; set; }

    [RelayCommand]
    private void Approve() => Approval?.Approve();

    [RelayCommand]
    private void Reject() => Approval?.Reject();

    private void OnApprovalResolved()
    {
        IsPending = false;

        var status = Approval?.Outcome switch
        {
            ToolApprovalOutcome.Approved => "已允许执行",
            ToolApprovalOutcome.Rejected => "已拒绝执行",
            _ => "已取消"
        };
        Text = $"{_baseText}\n→ {status}";
    }
}