// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoufflePet.Models;

namespace SoufflePet.ViewModels;

/// <summary>
/// 模型选择浮层里的一个模型条目：点击即切换为当前模型，是当前模型时高亮。
/// </summary>
public sealed partial class ChatModelPickerItem(ChatModelConfig config, ChatViewModel owner) : ObservableObject
{
    public ChatModelConfig Config { get; } = config;

    /// <summary>列表里显示的名字。</summary>
    public string Name => Config.Name;

    /// <summary>是否是当前选中的模型（浮层里高亮）。</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [RelayCommand]
    private void Select() => owner.SelectModel(Config);
}
