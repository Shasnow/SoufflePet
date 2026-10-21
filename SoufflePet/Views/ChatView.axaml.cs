// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SoufflePet.ViewModels;

namespace SoufflePet.Views;

/// <summary>
/// 对话面板本体（纯视图）：数据与命令来自 DataContext（ChatViewModel），
/// 代码后端只保留窗口级别、无法用绑定的交互——拖动无边框窗口与关闭请求。
/// </summary>
public partial class ChatView : UserControl
{
    public ChatView() => InitializeComponent();

    /// <summary>标题栏按住拖动：无边框窗口没有系统标题栏，用 BeginMoveDrag 接管。</summary>
    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        (TopLevel.GetTopLevel(this) as Window)?.BeginMoveDrag(e);
    }

    /// <summary>关闭按钮只是隐藏窗口，便于热键重新呼出（真正关闭由 ChatWindow.CloseForShutdown 负责）。</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e)
        => (TopLevel.GetTopLevel(this) as Window)?.Hide();

    /// <summary>点击列表项后收起浮层；重复点击当前模型时 SelectedItem 不变，也能正常关闭。</summary>
    private void OnModelListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is ChatViewModel vm) vm.IsModelPickerOpen = false;
    }
}
