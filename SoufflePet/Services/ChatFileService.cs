// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using Serilog;

namespace SoufflePet.Services;

/// <summary>桌宠要在对话窗口里直接展示的一个本地文件。</summary>
/// <param name="FilePath">本地绝对路径。</param>
/// <param name="FileName">展示文件名。</param>
/// <param name="IsImage">true 表示按图片预览，false 表示文件卡片。</param>
public sealed record ChatFileDisplay(string FilePath, string FileName, bool IsImage);

/// <summary>
/// 工具的「向对话窗口展示文件」通道：工具执行侧发起请求，对话界面插入图片或文件卡片。
/// 与 <see cref="UserQuestionService" /> 类似，界面不可用时只记日志，不影响工具返回。
/// </summary>
public sealed class ChatFileService
{
    private static readonly ILogger Logger = Log.ForContext<ChatFileService>();

    /// <summary>需要展示文件时触发；界面订阅后插入气泡。</summary>
    public event Action<ChatFileDisplay>? FileShown;

    public void Show(ChatFileDisplay display)
    {
        if (FileShown is { } handler)
        {
            handler(display);
            return;
        }

        Logger.Debug("show_local_file ignored: no UI subscriber");
    }
}
