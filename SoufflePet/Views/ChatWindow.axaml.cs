// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Serilog;
using SoufflePet.Models;
using SoufflePet.ViewModels;
using ChatMessage = SoufflePet.Models.ChatMessage;

namespace SoufflePet.Views;

/// <summary>
/// LLM 对话窗口：窗口属性（透明、无边框、置顶、固定尺寸）+ 对话面板。
/// 代码后端只保留无法用绑定的交互——拖动无边框窗口、Esc/关闭只隐藏、消息区自动滚动。
/// </summary>
public partial class ChatWindow : Window
{
    private static readonly ILogger Logger = Log.ForContext<ChatWindow>();

    /// <summary>关闭按钮/Alt+F4 只隐藏窗口，应用退出时由服务调 CloseForShutdown 真正关闭。</summary>
    private bool _allowClose;

    private ChatViewModel? _viewModel;

    /// <summary>WndProc 钩子只挂一次，避免每次 Opened 重复注册。</summary>
    private bool _resizeHitTestAttached;

    public ChatWindow()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;

        // TextBox 自己会在冒泡阶段处理 Enter（换行）并标记 Handled，
        // 所以这里用隧道阶段先拿到按键，才能实现 Enter 发送。
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);

        // 工作区浮层是独立顶层：焦点落在浮层内时窗口收不到 Esc，这里用隧道阶段兜住
        WorkspacePopupRoot.AddHandler(KeyDownEvent, OnWorkspacePopupKeyDown, RoutingStrategies.Tunnel);

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
            if (DataContext is ChatViewModel vm && vm.CloseWorkspaceOverlay()) return;
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
        // Opened 每次显示都可能触发，重复挂会让同一窗口注册多个回调，这里只挂一次
        if (_resizeHitTestAttached || !OperatingSystem.IsWindows()) return;
        _resizeHitTestAttached = true;
        Win32Properties.AddWndProcHookCallback(this, OnWndProcHitTest);
    }

    private IntPtr OnWndProcHitTest(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmNcHitTest) return IntPtr.Zero;

        // lParam 的低/高 16 位是屏幕坐标 x/y（物理像素，多屏时可能为负，按 short 取符号）
        var packed = lParam.ToInt64();
        var screen = new PixelPoint((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
        var point = this.PointToClient(screen);

        var left = point.X <= ResizeBorderThickness;
        var right = point.X >= ClientSize.Width - ResizeBorderThickness;
        var top = point.Y <= ResizeBorderThickness;
        var bottom = point.Y >= ClientSize.Height - ResizeBorderThickness;

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

    /// <summary>点击历史工作区条目：立即切换过去并关闭浮层。</summary>
    private void OnWorkspaceItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: WorkspaceItem workspace } && DataContext is ChatViewModel vm)
            vm.SwitchWorkspace(workspace);
    }

    /// <summary>点击条目右侧的删除按钮：先弹二次确认，确认后才移除记录。</summary>
    private void OnDeleteWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: WorkspaceItem workspace } && DataContext is ChatViewModel vm)
            vm.RequestDeleteWorkspace(workspace);
    }

    /// <summary>选择工作区：打开系统目录选择框，选好后加入历史并立即切换。</summary>
    private async void OnPickWorkspaceClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ChatViewModel vm) return;

        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择工作区",
                AllowMultiple = false
            });
            if (folders.Count == 0) return;

            vm.UseWorkspace(folders[0].Path.LocalPath);
        }
        catch (Exception ex)
        {
            // 选择器本身出错（平台对话框异常等）只记录，不能让它冒出去带崩进程
            Logger.Warning(ex, "pick workspace failed");
        }
    }

    /// <summary>浮层内的 Esc：先取消删除确认，再关闭浮层。</summary>
    private void OnWorkspacePopupKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (DataContext is ChatViewModel vm) vm.CloseWorkspaceOverlay();
    }

    /// <summary>添加附件：只记录绝对路径，随消息以 @路径 发送，模型可随时自行读取。</summary>
    private async void OnAttachClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ChatViewModel vm) return;
        vm.IsMoreOpen = false; // 先收起更多操作浮层，避免对话框关闭后它仍悬在窗口上

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择附件",
                AllowMultiple = true
            });
            if (files.Count == 0) return;

            vm.AddAttachments(files.Select(file => file.Path.LocalPath));
            InputBox.Focus();
        }
        catch (Exception ex)
        {
            // 选择器本身出错（平台对话框异常等）只记录，不能让它冒出去带崩进程
            Logger.Warning(ex, "pick attachment failed");
        }
    }

    /// <summary>点击附件标签上的叉：从待发送列表移除。</summary>
    private void OnRemoveAttachmentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChatAttachment attachment } && DataContext is ChatViewModel vm)
            vm.RemoveAttachment(attachment);
    }

    /// <summary>
    /// 输入框按键：输入框为空时 Backspace 整块删除最后一个附件；Enter 发送消息
    /// （Shift+Enter 等组合保留文本框的换行行为）。
    /// </summary>
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ChatViewModel vm) return;

        if (e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None && vm.Draft.Length == 0 && vm.HasAttachments)
        {
            e.Handled = true;
            vm.RemoveLastAttachment();
            return;
        }

        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        if (!vm.SendCommand.CanExecute(null)) return;

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