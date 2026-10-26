// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SoufflePet.Models;

namespace SoufflePet.Views;

/// <summary>
/// 定时任务管理面板本体（纯视图）：数据与命令来自 DataContext（ScheduledTasksViewModel），
/// 代码后端只保留窗口级别的交互——拖动无边框窗口、关闭（隐藏）请求与「触发方式」切换。
/// </summary>
public partial class ScheduledTasksView : UserControl
{
    public ScheduledTasksView() => InitializeComponent();

    /// <summary>标题栏按住拖动：无边框窗口没有系统标题栏，用 BeginMoveDrag 接管。</summary>
    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        (TopLevel.GetTopLevel(this) as Window)?.BeginMoveDrag(e);
    }

    /// <summary>关闭按钮只是隐藏窗口，真正关闭由 SettingsWindow.CloseForShutdown 负责。</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        (TopLevel.GetTopLevel(this) as Window)?.Hide();
    }

    /// <summary>「触发方式」切换：按钮的 Tag 就是目标调度类型，直接写回当前任务的 Kind（订阅会重排计划）。</summary>
    private void OnKindClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ScheduledTask task, Tag: string tag }) return;
        if (!Enum.TryParse<ScheduledTaskKind>(tag, out var kind)) return;
        task.Kind = kind;
    }
}