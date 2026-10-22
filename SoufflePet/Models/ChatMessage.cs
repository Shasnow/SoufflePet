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
        ToolApprovalRequest? approval = null, bool isExpanded = true)
    {
        Text = text;
        _baseText = text;
        IsUser = isUser;
        IsTool = isTool;
        IsReasoning = isReasoning;
        Approval = approval;
        IsExpanded = isExpanded;
        // 初始就是收起状态的思考气泡（从历史恢复）已经结束，标题直接显示「已完成」
        IsReasoningDone = !isExpanded;

        if (approval is null) return;

        // 请求可能在气泡插入前就已被取消（例如立刻按了停止）
        approval.Resolved += OnApprovalResolved;
        if (approval.Outcome == ToolApprovalOutcome.Pending) IsPending = true;
        else OnApprovalResolved();
    }

    [ObservableProperty]
    public partial string Text { get; set; }

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

    /// <summary>折叠面板的展开状态（思考过程与工具调用共用）：思考结束后自动收起，用户可手动切换。</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>思考阶段是否已结束（结束时自动折叠），与用户手动展开/收起无关。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReasoningHeader))]
    public partial bool IsReasoningDone { get; set; }

    /// <summary>折叠面板的标题：只反映思考是否结束，手动展开不会变回「思考中」。</summary>
    public string ReasoningHeader => IsReasoningDone ? "思考过程（已完成）" : "思考过程（思考中…）";

    /// <summary>工具名（仅工具记录）：折叠标题只显示它。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolHeader))]
    public partial string ToolName { get; set; } = string.Empty;

    /// <summary>工具参数原文（仅工具记录）：展开后显示。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolBody))]
    public partial string ToolArguments { get; set; } = string.Empty;

    /// <summary>工具执行结果（仅工具记录）：流式时先显示「执行中…」，展开后显示完整内容。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolBody))]
    public partial string ToolResult { get; set; } = string.Empty;

    /// <summary>工具气泡的折叠面板标题：只显示工具名，参数与结果展开后再看。</summary>
    public string ToolHeader => ToolName.Length > 0 ? $"调用工具：{ToolName}" : "调用工具";

    /// <summary>工具气泡的折叠面板内容：参数原文 + 完整结果；结果为空时给出提示。</summary>
    public string ToolBody
    {
        get
        {
            var result = ToolResult.Length > 0 ? ToolResult : "（无输出）";
            return ToolArguments.Length > 0 ? $"参数：{ToolArguments}\n结果：{result}" : $"结果：{result}";
        }
    }

    /// <summary>工具记录气泡：用折叠面板展示（待审批的气泡走普通文本，保证按钮始终可见）。</summary>
    public bool IsToolPanel => IsTool && Approval is null;

    /// <summary>普通文本气泡：既不是思考过程，也不是工具记录（待审批的气泡按普通消息展示）。</summary>
    public bool IsPlain => !IsReasoning && !IsToolPanel;

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