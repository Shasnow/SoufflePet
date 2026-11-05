// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SoufflePet.Core.Chat;

/// <summary>消息种类常量：与 docs/react-ui-contract.md 的 <c>ChatMessageKind</c> 一一对应。</summary>
public static class ChatMessageKinds
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Reasoning = "reasoning";
    public const string Tool = "tool";
    public const string Notice = "notice";
    public const string Attachment = "attachment";
    public const string Approval = "approval";
}

/// <summary>审批规则字符串：与契约里的 <c>ApprovalMode</c> 一致。</summary>
public static class ChatApprovalModes
{
    public const string Manual = "manual";
    public const string Full = "full";
}

/// <summary>会话内附件投影。宿主负责把图片路径换成 /local-file token URL。</summary>
public sealed record ChatAttachmentState(
    string Id,
    string Name,
    string Path,
    bool IsImage,
    string? Url = null,
    long? Size = null);

/// <summary>一条消息的 token 用量。</summary>
public sealed record ChatUsageState(
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    string? Model = null,
    DateTimeOffset? Time = null);

/// <summary>模型选择项。</summary>
public sealed record ChatModelState(string Key, string Provider, string Name, string ModelId);

/// <summary>工作区。</summary>
public sealed record ChatWorkspaceState(string Path, string Name);

/// <summary>提问面板的一个预设选项。</summary>
public sealed record ChatAskOptionState(string Text, string? Description = null);

/// <summary>提问面板的一道问题。</summary>
public sealed record ChatAskQuestionState(string Text, IReadOnlyList<ChatAskOptionState> Options);

/// <summary>当前待回答的提问。</summary>
public sealed record ChatAskState(string RequestId, IReadOnlyList<ChatAskQuestionState> Questions);

/// <summary>工具调用明细（气泡展开后显示）。</summary>
public sealed record ChatToolState(string Name, string Args, string Result);

/// <summary>审批气泡状态。</summary>
public sealed record ChatApprovalState(string ToolName, string Detail, bool Pending, string? Outcome = null);

/// <summary>聊天消息投影（只用于界面展示，不写回历史文件）。</summary>
public sealed record ChatMessageState(
    string Id,
    string Kind,
    string Text,
    DateTimeOffset Time,
    bool? Expanded = null,
    bool? Pending = null,
    bool? ReasoningDone = null,
    ChatToolState? Tool = null,
    ChatUsageState? Usage = null,
    ChatAttachmentState? Attachment = null,
    ChatApprovalState? Approval = null);

/// <summary>一个会话的完整快照：页面刷新或切回会话时据此重建界面。</summary>
public sealed record ChatSessionSnapshot(
    string SessionId,
    IReadOnlyList<ChatMessageState> Messages,
    string Draft,
    IReadOnlyList<ChatAttachmentState> Attachments,
    bool Generating,
    IReadOnlyList<ChatModelState> Models,
    string? SelectedModelKey,
    string ApprovalMode,
    ChatWorkspaceState? Workspace,
    IReadOnlyList<ChatWorkspaceState> Workspaces,
    ChatUsageState? Usage,
    ChatAskState? Ask);

/// <summary>会话索引里的一条摘要。</summary>
public sealed record SessionRecord(
    string Id,
    string Title,
    string? WorkspacePath,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MessageCount,
    string? LastUserText,
    string? ModelKey);

/// <summary>会话摘要变化（重命名/首条消息生成标题/一轮结束）。</summary>
public sealed record ChatSessionSummary(
    string Title,
    string? WorkspacePath,
    DateTimeOffset UpdatedAt,
    int MessageCount,
    string? LastUserText,
    string? ModelKey);

/// <summary>一批会话增量事件；宿主给整个事件加全局 rev。</summary>
public sealed record ChatOpBatch(string SessionId, IReadOnlyList<ChatOp> Ops);

/// <summary>会话列表事件 payload。</summary>
public sealed record ChatSessionListPayload(IReadOnlyList<SessionRecord> Sessions, string? ActiveId);

/// <summary>消息补丁：只允许契约里列出的字段。</summary>
public sealed record ChatMessagePatch(
    string? Kind = null,
    string? Text = null,
    bool? Expanded = null,
    bool? Pending = null,
    bool? ReasoningDone = null,
    ChatToolState? Tool = null,
    ChatUsageState? Usage = null,
    ChatAttachmentState? Attachment = null,
    ChatApprovalState? Approval = null);

/// <summary>会话增量操作（多态序列化的判别字段为 <c>op</c>）。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(ChatOpMessageAdd), "msg:add")]
[JsonDerivedType(typeof(ChatOpMessageInsert), "msg:insert")]
[JsonDerivedType(typeof(ChatOpMessageRemove), "msg:remove")]
[JsonDerivedType(typeof(ChatOpMessageText), "msg:text")]
[JsonDerivedType(typeof(ChatOpMessagePatchOp), "msg:patch")]
[JsonDerivedType(typeof(ChatOpTurn), "turn")]
[JsonDerivedType(typeof(ChatOpAttachments), "attachments")]
[JsonDerivedType(typeof(ChatOpApproval), "approval")]
[JsonDerivedType(typeof(ChatOpWorkspace), "workspace")]
[JsonDerivedType(typeof(ChatOpModels), "models")]
[JsonDerivedType(typeof(ChatOpAsk), "ask")]
[JsonDerivedType(typeof(ChatOpUsage), "usage")]
public abstract record ChatOp;

public sealed record ChatOpMessageAdd(ChatMessageState Msg) : ChatOp;

public sealed record ChatOpMessageInsert(int Index, ChatMessageState Msg) : ChatOp;

public sealed record ChatOpMessageRemove(string Id) : ChatOp;

public sealed record ChatOpMessageText(string Id, string Mode, string Text) : ChatOp;

public sealed record ChatOpMessagePatchOp(string Id, ChatMessagePatch Patch) : ChatOp;

public sealed record ChatOpTurn(bool Generating) : ChatOp;

public sealed record ChatOpAttachments(IReadOnlyList<ChatAttachmentState> Attachments) : ChatOp;

public sealed record ChatOpApproval(string Mode) : ChatOp;

public sealed record ChatOpWorkspace(ChatWorkspaceState? Workspace, IReadOnlyList<ChatWorkspaceState> Workspaces) : ChatOp;

public sealed record ChatOpModels(IReadOnlyList<ChatModelState> Models, string? SelectedModelKey) : ChatOp;

public sealed record ChatOpAsk(ChatAskState? Ask) : ChatOp;

public sealed record ChatOpUsage(ChatUsageState? Usage) : ChatOp;