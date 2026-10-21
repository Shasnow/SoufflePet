// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

namespace SoufflePet.ViewModels;

/// <summary>一条对话消息（不可变）：IsUser 决定气泡的左右对齐与配色。</summary>
/// <param name="Text">消息正文。</param>
/// <param name="IsUser">是否为用户发出的消息。</param>
public sealed class ChatMessageViewModel(string text, bool isUser)
{
    public string Text { get; } = text;

    public bool IsUser { get; } = isUser;
}
