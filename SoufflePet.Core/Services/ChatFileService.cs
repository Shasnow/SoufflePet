// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
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