// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
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

        // 拖放用代码注册：XAML 的附加事件（DragDrop.DragOver="..."）Rider 不会索引到处理函数，
        // 会把 OnWindowDragOver / OnWindowDrop 误报为未使用；这里直接引用后分析器即可识别
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);
        AddHandler(DragDrop.DropEvent, OnWindowDrop);

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

    /// <summary>拖入文件/文件夹时显示「复制」光标；拖的是文本等非文件内容时不接受。</summary>
    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    /// <summary>把拖入窗口的文件/文件夹加入待发送附件列表（文件夹同样记录其目录路径）。</summary>
    private void OnWindowDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ChatViewModel vm) return;
        // IStorageItem 同时覆盖文件（IStorageFile）与文件夹（IStorageFolder），两者都只取本地路径
        if (e.DataTransfer.TryGetFiles()?.OfType<IStorageItem>().ToList() is not { Count: > 0 } items) return;

        vm.AddAttachments(items.Select(item => item.Path.LocalPath));
        e.Handled = true;
        InputBox.Focus();
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
    /// 输入框按键：Ctrl+V 走自己的粘贴（剪贴板是图片时存成附件），输入框为空时 Backspace 整块删除最后一个附件，
    /// Enter 发送消息（Shift+Enter 等组合保留文本框的换行行为）。
    /// </summary>
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ChatViewModel vm) return;

        // 粘贴要先于文本框的内置处理：隧道阶段拦下，图片当附件、文本再按普通粘贴插回
        if (e is { Key: Key.V, KeyModifiers: KeyModifiers.Control })
        {
            e.Handled = true;
            _ = PasteFromClipboardAsync(vm);
            return;
        }

        if (e is { Key: Key.Back, KeyModifiers: KeyModifiers.None } && vm.Draft.Length == 0 && vm.HasAttachments)
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

    /// <summary>
    /// Ctrl+V：剪贴板里是图片时存成文件加入待发送附件（不把图片数据塞进正文）；
    /// 复制的是文件/文件夹时同样按路径加入附件；两者都没有才退回文本框原本的文本粘贴。
    /// </summary>
    private async Task PasteFromClipboardAsync(ChatViewModel vm)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        try
        {
            if (await clipboard.TryGetBitmapAsync() is { } bitmap)
            {
                using (bitmap)
                using (var stream = new MemoryStream())
                {
                    bitmap.Save(stream, PngBitmapEncoderOptions.Default);
                    vm.AddImageAttachment(stream.ToArray(), $"粘贴图片-{DateTime.Now:HHmmss}.png");
                }

                return;
            }

            // 资源管理器里复制的文件/文件夹：与拖放一致，只记路径
            if (await clipboard.TryGetFilesAsync() is { } items && items.Any())
            {
                vm.AddAttachments(items.Select(item => item.Path.LocalPath));
                return;
            }

            if (await clipboard.TryGetTextAsync() is { Length: > 0 } text)
            {
                var start = Math.Min(InputBox.SelectionStart, InputBox.SelectionEnd);
                InputBox.SelectedText = text;
                InputBox.SelectionStart = InputBox.SelectionEnd = start + text.Length;
            }
        }
        catch (Exception ex)
        {
            // 剪贴板内容可能被其他程序占用或格式异常，失败只记录，不影响继续输入
            Logger.Warning(ex, "paste clipboard content failed");
        }
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

    /// <summary>距底部多少像素以内仍算「贴底」，容忍滚动百分比取整带来的小数误差。</summary>
    private const double StickToBottomTolerance = 8;

    /// <summary>当前是否跟随到底部：用户主动向上滚后置为 false，滚回底部再恢复。</summary>
    private bool _stickToBottom = true;

    private bool _scrollChangedAttached;

    /// <summary>锚点校正最多看多少轮布局：偏移一变就会换一批实例化气泡，估算高度还会再变。</summary>
    private const int MaxAnchorPasses = 12;

    /// <summary>锚点校正的最长等待时间：估算值可能跨帧收敛，超过这个时间就不再追。</summary>
    private static readonly TimeSpan AnchorSettleWindow = TimeSpan.FromMilliseconds(600);

    /// <summary>正在做锚点校正：这期间由代码改的偏移不算用户滚动。</summary>
    private bool _keepingAnchor;

    /// <summary>
    /// 当前这次锚点校正的编号：连续展开两个气泡时，旧的那次校正要立刻作废，
    /// 否则两条校正链会互相拉扯。
    /// </summary>
    private int _anchorGeneration;

    /// <summary>校正的兜底超时：万一没等到布局事件，也不让 _keepingAnchor 一直卡住。</summary>
    private DateTime _anchorDeadline;

    /// <summary>正在等布局的那次校正（挂在 LayoutUpdated 上），新的展开会先摘掉它。</summary>
    private EventHandler? _anchorTick;

    /// <summary>判断消息区是否已贴底（内容不足一屏时也算贴底）。</summary>
    private bool IsAtBottom()
    {
        var remaining = MessageScroll.Extent.Height - MessageScroll.Viewport.Height - MessageScroll.Offset.Y;
        return remaining <= StickToBottomTolerance;
    }

    /// <summary>指定索引的气泡顶部相对消息区视口的位置；气泡没被实例化时返回 null。</summary>
    private double? BubbleTopInViewport(int index)
    {
        if (MessageItems.ContainerFromIndex(index) is not Control container) return null;
        return container.TranslatePoint(new Point(0, 0), MessageScroll)?.Y;
    }


    /// <summary>
    /// 折叠面板展开 / 收起会改变气泡高度。消息区是虚拟化的，没实例化的气泡按已实测气泡的平均高度估算，
    /// 展开一个大面板会把平均值拉高，同一个偏移就落到更早的气泡上——看起来像「跳回了之前的消息」。
    /// 这里记住气泡在视口里的位置，每次布局跑完把偏移改正回来，效果就是被点的气泡固定在原处、向下展开。
    /// </summary>
    private void KeepBubbleInPlace(ChatMessage message)
    {
        var index = _viewModel?.Messages.IndexOf(message) ?? -1;
        if (index < 0) return;

        // 必须在布局更新前量，此时拿到的还是展开前的旧位置
        if (BubbleTopInViewport(index) is not { } top) return;

        BeginAnchorCorrection(index, top);
    }

    /// <summary>
    /// 正文流式输出时气泡一直在长高，同样会把估算平均值带偏：用户没贴底（在回看历史）时
    /// 整个列表会一起漂，越流越远。这里拿视口里的气泡当锚点，把偏移钉在它当前的位置上。
    /// </summary>
    private void KeepViewportInPlace(ChatMessage changing)
    {
        var changingIndex = _viewModel?.Messages.IndexOf(changing) ?? -1;

        foreach (var container in MessageItems.GetRealizedContainers())
        {
            var index = MessageItems.IndexFromContainer(container);
            if (index < 0 || index == changingIndex) continue;      // 正在变高的气泡不能当锚点
            if (container.TranslatePoint(new Point(0, 0), MessageScroll)?.Y is not { } top) continue;

            BeginAnchorCorrection(index, top);
            return;
        }
    }

    /// <summary>
    /// 按锚点校正消息区偏移：把 <paramref name="index" /> 这条气泡钉在原来的视口位置
    /// <paramref name="targetTop" /> 上，差值收敛即收尾。
    /// </summary>
    private void BeginAnchorCorrection(int index, double targetTop)
    {
        var generation = ++_anchorGeneration;
        if (_anchorTick is not null) MessageScroll.LayoutUpdated -= _anchorTick; // 旧的校正作废

        _keepingAnchor = true;
        _anchorDeadline = DateTime.UtcNow + AnchorSettleWindow;

        // 内容坐标（视口位置 + 偏移）不随滚动变化：用它算漂移，用户自己滚动时不会被改回去，
        // 也不会把 ScrollIntoView、被夹到边界的偏移误当成用户滚动。
        var content = targetTop + MessageScroll.Offset.Y;
        var passes = 0;
        var stablePasses = 0;
        var needRestore = false;
        var needRebaseline = false;
        var scrolledIntoView = 0;

        void Finish()
        {
            if (generation != _anchorGeneration) return; // 已经有更新的一次校正接手
            if (_anchorTick is not null)
            {
                MessageScroll.LayoutUpdated -= _anchorTick;
                _anchorTick = null;
            }

            _keepingAnchor = false;
            // 校正后用户已经不在底部（展开出来的内容顶出去了），新的流式输出不再自动跟随，
            // 等他滚回底部再由 OnMessageScrollChanged 恢复
            _stickToBottom = IsAtBottom();
        }

        /// <summary>把偏移改到目标值；已经到位或顶到列表边界时返回 false。</summary>
        bool ApplyOffset(double desired)
        {
            var offset = MessageScroll.Offset;
            var max = Math.Max(0, MessageScroll.Extent.Height - MessageScroll.Viewport.Height);
            var next = Math.Clamp(desired, 0, max);
            if (Math.Abs(next - offset.Y) < 0.5) return false;

            MessageScroll.Offset = new Vector(offset.X, next);
            return true;
        }

        // 布局跑完才量：展开导致的重新布局、偏移变化引起的再次实例化，都会先落定再判
        void OnLayoutUpdated(object? sender, EventArgs e)
        {
            if (generation != _anchorGeneration) return;

            if (++passes > MaxAnchorPasses || DateTime.UtcNow > _anchorDeadline)
            {
                Finish();
                return;
            }

            if (BubbleTopInViewport(index) is not { } top)
            {
                // 估算值被一个大面板带偏时，同一个偏移会落到很多屏之外，锚点被回收了：
                // 先把它滚回取景范围（ScrollIntoView 会实例化它），下一轮布局再按内容坐标复位。
                if (scrolledIntoView >= 2)
                {
                    Finish();
                    return;
                }

                scrolledIntoView++;
                needRestore = true;
                MessageItems.ScrollIntoView(index);
                return;
            }

            var offset = MessageScroll.Offset.Y;

            if (needRestore)
            {
                // 找回来之后做一次绝对复位：把它放回原来的视口位置（ScrollIntoView 只是借道实例化）
                needRestore = false;
                if (ApplyOffset(top + offset - targetTop)) needRebaseline = true;
                else content = top + offset; // 没动偏移，基准就是现在
                return;
            }

            if (needRebaseline)
            {
                // 偏移刚改过，位置要等下一轮布局才更新，这里才拿到与新偏移对应的基准
                needRebaseline = false;
                content = top + offset;
                return;
            }

            // 漂移量：锚点在内容坐标里挪了多少（滚动不影响这个值）
            var drift = top + offset - content;
            if (Math.Abs(drift) < 0.5)
            {
                // 连续两轮都没变化才算稳定，避免估算值晚一拍才更新时提前收工
                if (++stablePasses >= 2) Finish();
                return;
            }

            stablePasses = 0;
            if (!ApplyOffset(offset + drift))
            {
                Finish(); // 已经顶到列表边界，再改偏移也校正不了
                return;
            }

            content += drift; // 记住它在内容坐标里的新位置，下一轮只在再漂移时才动
        }

        _anchorTick = OnLayoutUpdated;
        MessageScroll.LayoutUpdated += OnLayoutUpdated;
    }

    /// <summary>
    /// 更新跟随状态：只有用户向上滚（偏移变小）才算离开底部。
    /// 虚拟化下内容高度会分次补算、程序滚到底也会让偏移变大，这些都不应取消跟随。
    /// </summary>
    private void OnMessageScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // 锚点校正期间改偏移是代码行为，不是用户滚动；超时后自动放行，避免状态卡死
        if (_keepingAnchor)
        {
            if (DateTime.UtcNow < _anchorDeadline) return;
            _keepingAnchor = false;
        }

        if (IsAtBottom())
        {
            _stickToBottom = true;
            return;
        }

        if (e.OffsetDelta.Y < 0)
        {
            _stickToBottom = false;
            return;
        }

        // 内容高度补算后仍贴底意愿不变，补滚一次，避免停在估算高度处
        if (_stickToBottom && e.ExtentDelta.Y > 0) ScrollToEnd();
    }

    /// <summary>首次显示时恢复的历史消息不会触发集合变更事件，需要在布局完成后主动滚到底部。</summary>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (!_scrollChangedAttached)
        {
            MessageScroll.ScrollChanged += OnMessageScrollChanged;
            _scrollChangedAttached = true;
        }

        _stickToBottom = true;
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

        // 清空或切换会话时重新回到跟随状态
        if (e.Action == NotifyCollectionChangedAction.Reset) _stickToBottom = true;

        if (_stickToBottom) ScrollToEnd();
    }

    /// <summary>
    /// 流式追加时，只有用户停留在底部才跟随滚动，避免正在回看时气泡被刷走；
    /// 没贴底时改为按锚点把视口钉住——正文把气泡撑高同样会带偏虚拟化的估算高度，任由它漂就是「跳回之前的消息」。
    /// 展开 / 收起折叠面板时同理，只是锚点换成被点的那个气泡。
    /// </summary>
    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ChatMessage message) return;

        if (e.PropertyName == nameof(ChatMessage.Text))
        {
            if (_stickToBottom) ScrollToEnd();
            else KeepViewportInPlace(message);
            return;
        }

        if (e.PropertyName == nameof(ChatMessage.IsExpanded)) KeepBubbleInPlace(message);
    }

    /// <summary>等布局完成后再滚，否则新气泡（或虚拟化补算的高度）还没算进去。</summary>
    private void ScrollToEnd()
    {
        Dispatcher.UIThread.Post(() => MessageScroll.ScrollToEnd(), DispatcherPriority.Background);
    }
}