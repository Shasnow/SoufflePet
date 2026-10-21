// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using CommunityToolkit.Mvvm.ComponentModel;

namespace SoufflePet.ViewModels;

/// <summary>一条对话消息：IsUser 决定气泡的左右对齐与配色；Text 支持流式追加时刷新。</summary>
public sealed partial class ChatMessageViewModel : ObservableObject
{
    public ChatMessageViewModel(string text, bool isUser)
    {
        Text = text;
        IsUser = isUser;
    }

    [ObservableProperty]
    public partial string Text { get; set; }

    public bool IsUser { get; }
}
