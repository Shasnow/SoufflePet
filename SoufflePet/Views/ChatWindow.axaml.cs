// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SoufflePet.ViewModels;
using ChatMessage = SoufflePet.Models.ChatMessage;

namespace SoufflePet.Views;

/// <summary>
/// LLM 对话窗口：窗口属性（透明、无边框、置顶、可缩放）+ 对话面板。
/// 代码后端只保留无法用绑定的交互——拖动/缩放无边框窗口、Esc/关闭只隐藏、消息区自动滚动。
/// </summary>
public partial class ChatWindow : Window
{
    /// <summary>关闭按钮/Alt+F4 只隐藏窗口，应用退出时由服务调 CloseForShutdown 真正关闭。</summary>
    private bool _allowClose;

    /// <summary>命中钩子只挂一次：Opened 在每次 Hide/Show 后都会再触发。</summary>
    private bool _resizeHookAttached;

    private ChatViewModel? _viewModel;

    public ChatWindow()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;

        // TextBox 自己会在冒泡阶段处理 Enter（换行）并标记 Handled，
        // 所以这里用隧道阶段先拿到按键，才能实现 Enter 发送。
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);

        Opened += (_, _) =>
        {
            FocusInput();
            AttachResizeHitTest();
        };
        Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            Hide();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Hide();
        };
    }

    /// <summary>聚焦输入框。</summary>
    public void FocusInput() => this.FindDescendantOfType<TextBox>()?.Focus();

    /// <summary>应用退出时真正关闭窗口（平时关闭只隐藏，便于热键重新呼出）。</summary>
    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    /// <summary>标题栏按住拖动：无边框窗口没有系统标题栏，用 BeginMoveDrag 接管。</summary>
    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        BeginMoveDrag(e);
    }

    /// <summary>窗口最外圈用于缩放的感应宽度（DIP）。</summary>
    private const double ResizeBorderThickness = 6;

    private const uint WmNcHitTest = 0x0084;

    // WM_NCHITTEST 的返回值，命名与 Win32 的 HT* 常量一致
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    /// <summary>
    /// 把窗口最外圈的命中测试交给系统：在 WM_NCHITTEST 里按鼠标位置返回 HTLEFT/HTTOP…，
    /// 之后缩放拖拽、鼠标指针形状、Aero Snap 都由 DefWindowProc 处理，
    /// 不用在界面上盖一圈透明控件抢点击。
    /// </summary>
    private void AttachResizeHitTest()
    {
        // Opened 每次呼出窗口都会再次触发，钩子只挂一次，避免回调越叠越多
        if (_resizeHookAttached || !OperatingSystem.IsWindows()) return;

        Win32Properties.AddWndProcHookCallback(this, OnWndProcHitTest);
        _resizeHookAttached = true;
    }

    private IntPtr OnWndProcHitTest(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmNcHitTest) return IntPtr.Zero;

        // lParam 的低/高 16 位是屏幕坐标 x/y（物理像素，多屏时可能为负，按 short 取符号）
        var packed = lParam.ToInt64();
        var screen = new PixelPoint((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
        var point = this.PointToClient(screen);

        // PointToClient 返回物理像素，而 ClientSize 与 ResizeBorderThickness 都是 DIP：
        // 先把鼠标位置换算成 DIP，否则高缩放下感应区会偏到画面内侧（右侧与底边尤其明显）。
        var scaling = RenderScaling > 0 ? RenderScaling : 1.0;
        var x = point.X / scaling;
        var y = point.Y / scaling;

        var left = x <= ResizeBorderThickness;
        var right = x >= ClientSize.Width - ResizeBorderThickness;
        var top = y <= ResizeBorderThickness;
        var bottom = y >= ClientSize.Height - ResizeBorderThickness;

        var hit = (left, right, top, bottom) switch
        {
            (true, _, true, _) => HtTopLeft,
            (_, true, true, _) => HtTopRight,
            (true, _, _, true) => HtBottomLeft,
            (_, true, _, true) => HtBottomRight,
            (true, _, _, _) => HtLeft,
            (_, true, _, _) => HtRight,
            (_, _, true, _) => HtTop,
            (_, _, _, true) => HtBottom,
            _ => 0
        };

        if (hit == 0) return IntPtr.Zero;

        handled = true;
        return new IntPtr(hit);
    }

    /// <summary>关闭按钮只是隐藏窗口，便于热键重新呼出（真正关闭由 CloseForShutdown 负责）。</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Hide();

    /// <summary>点击列表项后收起浮层；重复点击当前模型时 SelectedItem 不变，也能正常关闭。</summary>
    private void OnModelListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is ChatViewModel vm) vm.IsModelPickerOpen = false;
    }

    /// <summary>Enter 发送消息；Shift+Enter（或其他修饰键组合）保留文本框的换行行为。</summary>
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        if (DataContext is not ChatViewModel vm || !vm.SendCommand.CanExecute(null)) return;

        e.Handled = true;
        vm.SendCommand.Execute(null);
    }

    /// <summary>提问面板的自由输入框按 Enter 直接翻到下一题（最后一题停留，等用户提交）。</summary>
    private void OnAnswerInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is not ChatViewModel { AskUser: { } panel }) return;

        e.Handled = true;
        if (panel.NextCommand.CanExecute(null)) panel.NextCommand.Execute(null);
    }

    /// <summary>跟随 DataContext 换绑消息订阅。</summary>
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

    /// <summary>首次显示时恢复的历史消息不会触发集合变更事件，需要在布局完成后主动滚到底部。</summary>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        ScrollToEnd();
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (ChatMessage message in e.OldItems)
                message.PropertyChanged -= OnMessagePropertyChanged;

        if (e.NewItems is not null)
            foreach (ChatMessage message in e.NewItems)
                message.PropertyChanged += OnMessagePropertyChanged;

        ScrollToEnd();
    }

    /// <summary>流式追加时跟随滚动到底部，避免新内容跑出可视区。</summary>
    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatMessage.Text)) ScrollToEnd();
    }

    /// <summary>等布局完成后再滚，否则新气泡的高度还没算进去。</summary>
    private void ScrollToEnd()
    {
        Dispatcher.UIThread.Post(() => MessageScroll.ScrollToEnd(), DispatcherPriority.Background);
    }
}