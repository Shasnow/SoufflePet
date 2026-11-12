// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.Collections.Generic;

namespace SoufflePet.Services.Web;

/// <summary>设置面板的 v1 DTO（与 web/src/bridge/contracts.ts 对齐）。</summary>
public sealed record ChatProviderModelDto(string ModelId, string? Name, bool IsActive);

public sealed record ProviderDto(
    string Name,
    string BaseUrl,
    string ApiKey,
    IReadOnlyList<ChatProviderModelDto> Models);

public sealed record ProviderInputDto(
    string Name,
    string BaseUrl,
    string ApiKey,
    IReadOnlyList<ChatProviderModelDto> Models,
    string? PreviousName = null);

/// <summary>人设面板：桌宠称呼与系统提示词；default* 由宿主生成，供前端恢复默认。</summary>
public sealed record PersonaDto(
    string PetName,
    string SystemPrompt,
    string DefaultPetName,
    string DefaultSystemPrompt);

public sealed record PersonaInputDto(string PetName, string SystemPrompt);

public sealed record McpServerDto(
    string Name,
    bool Enabled,
    string Transport,
    string Command,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    Dictionary<string, string> Environment,
    string Url,
    Dictionary<string, string> Headers,
    int TimeoutSeconds,
    string? PreviousName = null);

public sealed record McpToolDto(string Name, string Description);

public sealed record SkillDto(
    string Name,
    string Description,
    string Source,
    string FilePath,
    bool Enabled,
    string? Content = null);

public sealed record ConfigFieldDto(
    string Key,
    string Label,
    string? Description,
    string Kind,
    IReadOnlyList<string>? Options,
    object? Value);

public sealed record PluginDto(
    string Id,
    string Name,
    string Version,
    string Author,
    string Description,
    bool BuiltIn,
    string Directory,
    string State,
    string? Error,
    IReadOnlyList<string> ToolNames,
    int FailureCount,
    bool HasConfig);

public sealed record TaskDto(
    string Id,
    string Name,
    string Content,
    bool Enabled,
    string Kind,
    string? OnceDate,
    string? TimeOfDay,
    IReadOnlyList<int> Weekdays,
    int IntervalMinutes,
    bool Completed,
    string? NextRunAt,
    string? LastRunAt,
    string? WorkspacePath,
    string? ModelKey);

public sealed record TaskInputDto(
    string? Id,
    string Name,
    string Content,
    bool Enabled,
    string Kind,
    string? OnceDate,
    string? TimeOfDay,
    IReadOnlyList<int> Weekdays,
    int IntervalMinutes,
    string? WorkspacePath,
    string? ModelKey);

public sealed record ScheduledTaskLogDto(string Text, bool IsError);

public sealed record Live2DModelDto(
    string Name,
    string Directory,
    bool Active,
    string? GameExpression,
    string? MusicExpression,
    bool Valid,
    string? Error);