// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoufflePet.Services.Web;

/// <summary>
/// React UI 的 v1 DTO（与 docs/react-ui-contract.md 及 web/src/bridge/contracts.ts 对齐）。
/// 记录类型只用于桥接投影，不参与业务状态；字段名统一走 camelCase。
/// </summary>
public sealed record PetDto(string Name, string Initial);

public sealed record ThemeDto(
    string Mode,
    string Accent,
    double AccentL,
    double AccentC,
    double AccentH,
    bool FollowSystem);

public sealed record GlobalUsageDto(
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    long ConversationCount,
    long PokeCount,
    long HeadPatCount,
    long RunSeconds);

public sealed record AttachmentDto(
    string Id,
    string Name,
    string Path,
    bool IsImage,
    string? Url = null,
    long? Size = null);

public sealed record UsageDto(
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    string? Model = null,
    string? Time = null);

public sealed record ChatModelDto(string Key, string Provider, string Name, string ModelId);

public sealed record WorkspaceDto(string Path, string Name);

public sealed record AskOptionDto(string Text, string? Description = null);

public sealed record AskQuestionDto(string Text, IReadOnlyList<AskOptionDto> Options);

public sealed record AskStateDto(string RequestId, IReadOnlyList<AskQuestionDto> Questions);

public sealed record ToolDetailDto(string Name, string Args, string Result);

public sealed record ApprovalDetailDto(
    string ToolName,
    string Detail,
    bool Pending,
    string? Outcome = null);

public sealed record ChatMessageDto(
    string Id,
    string Kind,
    string Text,
    string Time,
    bool? Expanded = null,
    bool? Pending = null,
    bool? ReasoningDone = null,
    ToolDetailDto? Tool = null,
    UsageDto? Usage = null,
    AttachmentDto? Attachment = null,
    ApprovalDetailDto? Approval = null);

public sealed record ChatSnapshotDto(
    string SessionId,
    IReadOnlyList<ChatMessageDto> Messages,
    string Draft,
    IReadOnlyList<AttachmentDto> Attachments,
    bool Generating,
    IReadOnlyList<ChatModelDto> Models,
    string? SelectedModelKey,
    string ApprovalMode,
    WorkspaceDto? Workspace,
    IReadOnlyList<WorkspaceDto> Workspaces,
    UsageDto? Usage,
    AskStateDto? Ask);

public sealed record SessionSummaryDto(
    string Id,
    string Title,
    string? WorkspacePath,
    string? WorkspaceName,
    string CreatedAt,
    string UpdatedAt,
    int MessageCount,
    string? LastUserText,
    string? ModelKey,
    bool Generating);

public sealed record AppSnapshotDto(
    int ProtocolVersion,
    string Window,
    string? Route,
    PetDto Pet,
    ThemeDto Theme,
    GlobalUsageDto Usage,
    IReadOnlyList<SessionSummaryDto> Sessions,
    string? ActiveSessionId,
    ChatSnapshotDto? Chat,
    IReadOnlyList<WorkspaceDto> Workspaces);

public sealed record WindowStateDto(bool Visible, bool Maximized);

/// <summary>响应 / 事件的统一序列化选项。</summary>
public static class WebJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);
}