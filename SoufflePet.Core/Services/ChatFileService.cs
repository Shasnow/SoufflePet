// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Serilog;
using SoufflePet.Core.Chat;

namespace SoufflePet.Services;

/// <summary>桌宠要在对话窗口里直接展示的一个本地文件。</summary>
/// <param name="FilePath">本地绝对路径。</param>
/// <param name="FileName">展示文件名。</param>
/// <param name="IsImage">true 表示按图片预览，false 表示文件卡片。</param>
public sealed record ChatFileDisplay(string FilePath, string FileName, bool IsImage);

/// <summary>工具编辑文件后交给界面展示的差异；CallId 用于把差异贴回对应的工具气泡。</summary>
public sealed record ChatFileEdit(string CallId, IReadOnlyList<ChatFileDiffState> Diffs);

/// <summary>
/// 工具的「向对话窗口展示文件」通道：展示文件与编辑差异都从这里进入界面。
/// 与 <see cref="UserQuestionService" /> 类似，界面不可用时只记日志，不影响工具返回。
/// </summary>
public sealed class ChatFileService
{
    private static readonly ILogger Logger = Log.ForContext<ChatFileService>();

    /// <summary>需要展示文件时触发；界面订阅后插入气泡。</summary>
    public event Action<ChatFileDisplay>? FileShown;

    /// <summary>工具完成文件编辑时触发；界面据此把 diff 贴到当前工具气泡。</summary>
    public event Action<ChatFileEdit>? FileEdited;

    public void Show(ChatFileDisplay display)
    {
        if (FileShown is { } handler)
        {
            handler(display);
            return;
        }

        Logger.Debug("show_local_file ignored: no UI subscriber");
    }

    /// <summary>展示成功时 show_file 的返回前缀：重启恢复历史时据此判断这条工具记录当时是否插过文件气泡。</summary>
    internal const string ShownResultPrefix = "已在对话窗口展示";

    /// <summary>show_file 展示成功时的返回文案；前缀与恢复判定共用同一常量，改文案不会让历史还原静默失效。</summary>
    internal static string ShownFileResult(string name, bool isImage) =>
        $"{ShownResultPrefix}{(isImage ? "图片" : "文件")}「{name}」。";

    /// <summary>
    /// 从历史里的工具记录还原「展示文件」气泡：工具是 <c>show_file</c> 且当次调用确实展示成功时返回展示信息，
    /// 否则返回 null（参数无效、文件超限或读取失败时当时并没有插入气泡）。
    /// 相对路径按会话工作区解析，与工具调用时的规则一致；文件事后被删也照常还原，由界面按路径展示。
    /// </summary>
    public static ChatFileDisplay? RestoreShownFile(
        string? toolName,
        string? arguments,
        string? result,
        string? workspacePath = null)
    {
        if (!string.Equals(toolName, "show_file", StringComparison.OrdinalIgnoreCase)) return null;
        if (result is null || !result.StartsWith(ShownResultPrefix, StringComparison.Ordinal)) return null;

        try
        {
            using var document = JsonDocument.Parse(arguments ?? string.Empty);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            var raw = document.RootElement.TryGetProperty("path", out var pathElement)
                ? pathElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(raw)) return null;

            raw = raw.Trim();
            if (raw.StartsWith('@')) raw = raw[1..].Trim().Trim('"');
            if (raw.Length == 0) return null;

            var fullPath = WorkspaceContext.Resolve(raw, workspacePath);
            var display = document.RootElement.TryGetProperty("displayName", out var nameElement)
                ? nameElement.GetString()?.Trim()
                : null;
            var name = string.IsNullOrEmpty(display) ? Path.GetFileName(fullPath) : display;
            var isImage = ChatTools.ImageExtensions.Contains(Path.GetExtension(fullPath));
            return new ChatFileDisplay(fullPath, name, isImage);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "restore shown file failed args={Arguments}", arguments);
            return null;
        }
    }

    public void RecordEdit(ChatFileEdit edit)
    {
        if (edit.Diffs.Count == 0) return;
        if (FileEdited is { } handler)
        {
            handler(edit);
            return;
        }

        Logger.Debug("file edit ignored: no UI subscriber path={Path}", edit.Diffs[0].Path);
    }
}
