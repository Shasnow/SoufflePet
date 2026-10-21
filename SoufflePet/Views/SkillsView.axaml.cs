// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SoufflePet.Views;

/// <summary>
/// 技能管理面板本体（纯视图）：数据与命令来自 DataContext（SkillsViewModel），
/// 代码后端只保留窗口级别的交互——拖动无边框窗口与关闭（隐藏）请求。
/// </summary>
public partial class SkillsView : UserControl
{
    public SkillsView() => InitializeComponent();

    /// <summary>标题栏按住拖动：无边框窗口没有系统标题栏，用 BeginMoveDrag 接管。</summary>
    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        (TopLevel.GetTopLevel(this) as Window)?.BeginMoveDrag(e);
    }

    /// <summary>关闭按钮只是隐藏窗口，真正关闭由 SkillsWindow.CloseForShutdown 负责。</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        (TopLevel.GetTopLevel(this) as Window)?.Hide();
    }
}
