// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using CommunityToolkit.Mvvm.ComponentModel;

namespace SoufflePet.Models;

/// <summary>一条对话消息：IsUser 决定气泡的左右对齐与配色；IsTool 表示工具调用记录（不进入请求历史）；Text 支持流式追加时刷新。</summary>
public sealed partial class ChatMessage : ObservableObject
{
    public ChatMessage(string text, bool isUser, bool isTool = false)
    {
        Text = text;
        IsUser = isUser;
        IsTool = isTool;
    }

    [ObservableProperty]
    public partial string Text { get; set; }

    public bool IsUser { get; }

    /// <summary>工具调用记录（弱化样式展示，不参与上下文）。</summary>
    public bool IsTool { get; }
}
