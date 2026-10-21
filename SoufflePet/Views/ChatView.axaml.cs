// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SoufflePet.ViewModels;

namespace SoufflePet.Views;

/// <summary>
/// 对话面板本体（纯视图）：数据与命令来自 DataContext（ChatViewModel），
/// 代码后端只保留窗口级别、无法用绑定的交互——拖动无边框窗口、关闭请求与消息区自动滚动。
/// </summary>
public partial class ChatView : UserControl
{
    private ChatViewModel? _viewModel;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

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

    /// <summary>跟随 DataContext 换绑消息订阅（ChatView 由 ViewLocator 创建，可能换过 VM）。</summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.Messages.CollectionChanged -= OnMessagesCollectionChanged;
            foreach (var message in _viewModel.Messages)
                message.PropertyChanged -= OnMessagePropertyChanged;
        }

        _viewModel = DataContext as ChatViewModel;
        if (_viewModel is null) return;

        _viewModel.Messages.CollectionChanged += OnMessagesCollectionChanged;
        foreach (var message in _viewModel.Messages)
            message.PropertyChanged += OnMessagePropertyChanged;
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (ChatMessageViewModel message in e.OldItems)
                message.PropertyChanged -= OnMessagePropertyChanged;

        if (e.NewItems is not null)
            foreach (ChatMessageViewModel message in e.NewItems)
                message.PropertyChanged += OnMessagePropertyChanged;

        ScrollToEnd();
    }

    /// <summary>流式追加时跟随滚动到底部，避免新内容跑出可视区。</summary>
    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatMessageViewModel.Text)) ScrollToEnd();
    }

    /// <summary>等布局完成后再滚，否则新气泡的高度还没算进去。</summary>
    private void ScrollToEnd()
        => Dispatcher.UIThread.Post(() => MessageScroll.ScrollToEnd(), DispatcherPriority.Background);
}
