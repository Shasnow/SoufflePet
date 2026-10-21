// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Serilog;
using SoufflePet.Models;
using SoufflePet.native;
using SoufflePet.Services;
using SoufflePet.Utils;

namespace SoufflePet.Views;

/// <summary>原生渲染版桌宠窗口：Live2DCSharpSDK + OpenGL，不依赖 WebView。</summary>
public class NativePetWindow : Window, IPetWindow
{
    private static readonly ILogger Logger = Log.ForContext<NativePetWindow>();

    private readonly NativePetControl _control;
    private readonly DispatcherTimer _renderTimer;
    private readonly IChatWindowService _chat;
    private DateTime _lastMoveLog;

    /// <summary>帧率采样起点与期间累计的渲染帧数（每满 1 秒刷新一次显示）。</summary>
    private DateTime _fpsSampleTime = DateTime.Now;
    private int _fpsAccum;

    /// <summary>右上角帧率叠加层（当前帧率），托盘"显示帧率"开关控制显隐。</summary>
    private readonly Border _fpsBadge;
    private readonly TextBlock _fpsText;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private const uint WmNcLButtonDown = 0x00A1;
    private static readonly IntPtr HtCaption = new(2);

    private const int GwlExstyle = -20;
    private const long WsExTransparent = 0x00000020;
    private const long WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x2;
    private const int VkLbutton = 0x01;
    private const int VkRbutton = 0x02;
    private const int VkMbutton = 0x04;
    private bool _clickThrough;

    /// <summary>目光跟随开关（托盘勾选项）：关闭时不再把光标坐标喂给控制层。</summary>
    private bool _gazeFollow = true;

    /// <summary>是否已完成首次定位（Opened 每次显示都会触发，位置只设一次）。</summary>
    private bool _positioned;

    /// <summary>显示器电源状态（false=屏已关）：关屏期间停渲染链，亮屏恢复。</summary>
    private bool _displayOn = true;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    public NativePetWindow(SouffleSettings settings, ILipSyncService lipSync, IGamepadService gamepad,
        IChatWindowService chat)
    {
        _chat = chat;
        Title = "SoufflePet";
        Width = 480;
        Height = 680;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        WindowDecorations = WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _control = new NativePetControl(settings);

        // 右上角帧率叠加层：半透明底保证任意背景下可读，不参与命中检测（穿透判定读的是 GL 帧缓冲）
        _fpsText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 12,
            Text = "0",
        };
        _fpsBadge = new Border
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 10, 0),
            Background = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Child = _fpsText,
        };
        Content = new Grid { Children = { _control, _fpsBadge } };

        // 定时器只负责输入侧与统计，不驱动 GL 重绘：重绘由渲染回调自续帧
        // （跟随合成器帧时钟 / 显示器 vsync），不限帧。
        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _renderTimer.Tick += (_, _) =>
        {
            if (!_displayOn) return; // 关屏期间停掉输入轮询/统计（渲染链同样已停）
            TrackGlobalCursor();
            UpdateClickThrough();
            UpdateFps();                                        // 每满 1 秒刷新一次帧率显示
            _control.SetLipSync(lipSync.CurrentLevel);          // 口型律动音量每帧推给渲染层
            _control.SetLipSyncWide(lipSync.CurrentWideLevel);  // 口型水平宽窄每帧推给渲染层
            _control.SetGamepadInput(gamepad.State);            // 手柄输入快照喂给渲染层（服务内部 30Hz 轮询）
        };
        _renderTimer.Start();

        // 窗口可见性 或 显示器电源：两者都允许才拉起渲染链（关屏/隐藏时停，恢复时立即拉起）。
        // 用 ControlRenderLoop 而非直接读 IsVisible，状态门控集中在一处，避免两条路径互相覆盖
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty) ControlRenderLoop();
        };

        // 交互：
        // - 左键按住拖动（位移 > 6px）→ 系统级拖拽窗口
        // - 左键单击 → 随机切换表情
        // - 右键 → 呼出 / 收起 LLM 对话窗口
        // - 滚轮 → 缩放模型
        // - 中键按住拖动 → 移动模型在窗口内的位置（不移动窗口）
        Point? pressPoint = null;
        Point? modelDragPoint = null;
        PointerPressed += (_, e) =>
        {
            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsLeftButtonPressed)
            {
                pressPoint = e.GetPosition(this);
            }
            else if (props.IsRightButtonPressed)
            {
                _chat.Toggle(this);
            }
            else if (props.IsMiddleButtonPressed)
            {
                modelDragPoint = e.GetPosition(this);
                e.Pointer.Capture(this); // 光标拖出窗口边界后仍能收到移动事件
            }
        };
        PointerMoved += (_, e) =>
        {
            // 目光跟随由 TrackGlobalCursor 全局轮询负责（PointerMoved 只在窗口内触发），
            // 这里只处理按住拖动判定。
            var pos = e.GetPosition(this);

            // 中键拖模型：按 DIP 增量移动模型显示位置
            if (modelDragPoint is not null)
            {
                _control.MoveModelBy(
                    (float)(pos.X - modelDragPoint.Value.X),
                    (float)(pos.Y - modelDragPoint.Value.Y));
                modelDragPoint = pos;
                return;
            }

            if ((DateTime.Now - _lastMoveLog).TotalMilliseconds > 800)
            {
                _lastMoveLog = DateTime.Now;
                Logger.Debug("PointerMoved pos={PosX:F0},{PosY:F0} bounds={Width:F0}x{Height:F0}", pos.X, pos.Y, Bounds.Width, Bounds.Height);
            }

            if (pressPoint is null) return;
            var dx = pos.X - pressPoint.Value.X;
            var dy = pos.Y - pressPoint.Value.Y;
            if (dx * dx + dy * dy < 36) return; // 位移 < 6px 不算拖拽
            pressPoint = null;                 // 进入系统拖拽模态后不再响应单击
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero) return;
            ReleaseCapture();
            SendMessage(hwnd, WmNcLButtonDown, HtCaption, IntPtr.Zero);
        };
        PointerReleased += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonReleased)
            {
                modelDragPoint = null;
                e.Pointer.Capture(null);
                return;
            }
            // 右键在按下时已呼出对话窗口，释放这里不再触发其他动作
            if (e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonReleased) return;
            if (pressPoint is null) return;
            pressPoint = null;
            _control.TapRandomExpression();
        };
        PointerWheelChanged += (_, e) =>
        {
            Logger.Debug("Wheel delta={Delta}", e.Delta.Y);
            _control.Zoom(e.Delta.Y > 0 ? 1.1f : 1f / 1.1f);
        };

        Opened += (_, _) =>
        {
            // WS_EX_TRANSPARENT 只对分层窗口生效，而 Avalonia 12 的透明窗口并不挂
            // WS_EX_LAYERED——先补挂上并设不透明度 255（不影响正常渲染），
            // 之后点击穿透只切换 WS_EX_TRANSPARENT 即可。
            EnsureLayered();

            // Opened 在每次 Hide 后再 Show 时都会触发，只在首次显示时定位到右下角，
            // 避免托盘切换显示/隐藏把用户拖动过的窗口位置复原
            if (_positioned) return;
            _positioned = true;

            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen is null) return;
            var wa = screen.WorkingArea;
            // Position 是物理像素，Bounds 是 DIP，必须乘 RenderScaling（125% 屏 → 1.25）：
            // 否则按 DIP 尺寸算出的定位偏小，窗口底部/右侧会超出显示器
            var scaling = RenderScaling <= 0 ? 1.0 : RenderScaling;
            Position = new PixelPoint(
                wa.Right - (int)(Bounds.Width * scaling),
                wa.Bottom - (int)(Bounds.Height * scaling));
        };
    }

    /// <summary>
    /// 全局鼠标追踪：Avalonia 的 PointerMoved 只在光标位于窗口内时触发，移出窗口后
    /// 视线会定住。这里用 GetCursorPos 轮询屏幕光标（ClientToScreen 换算窗口客户区
    /// 原点），光标在窗口外也持续驱动目光跟随，超出窗口的坐标夹到 [-1,1]。
    /// </summary>
    private void TrackGlobalCursor()
    {
        if (!_gazeFollow || !IsVisible || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (!GlobalCursor.TryGetClientPos(hwnd, out var px, out var py)) return;

        // 光标坐标是物理像素，Bounds 是 DIP，先按 RenderScaling 换算
        var scaling = RenderScaling <= 0 ? 1.0 : RenderScaling;
        var x = (float)(px / scaling / Bounds.Width * 2 - 1);
        var y = (float)(py / scaling / Bounds.Height * 2 - 1);

        // 裁剪坐标：中心原点、y 向上；窗口外光标夹到边界。
        // 基准点（模型双眼中心）的换算在 NativePetControl.OnDrag 内完成。
        _control.OnDrag(Math.Clamp(x, -1f, 1f), -Math.Clamp(y, -1f, 1f));
    }

    /// <summary>
    /// 确保窗口带 WS_EX_LAYERED：WS_EX_TRANSPARENT 的鼠标穿透只在分层窗口上生效。
    /// 设不透明度 255 不改变现有渲染效果。
    /// </summary>
    private void EnsureLayered()
    {
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
        {
            Logger.Warning("EnsureLayered skipped, hwnd is zero");
            return;
        }
        var style = GetWindowLongPtr(hwnd, GwlExstyle).ToInt64();
        if ((style & WsExLayered) != 0) return;
        SetWindowLongPtr(hwnd, GwlExstyle, (IntPtr)(style | WsExLayered));
        var ok = SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);
        Logger.Information("WS_EX_LAYERED applied hwnd={Hwnd} style=0x{Style:X} sla={Sla}", hwnd, style | WsExLayered, ok);
    }

    /// <summary>
    /// 鼠标穿透：光标没压在模型可见像素上时给窗口挂 WS_EX_TRANSPARENT，点击落到
    /// 下层应用；压在模型上（或按住鼠标期间）恢复交互。命中判定由
    /// NativePetControl 每帧读帧缓冲像素得出。
    /// </summary>
    private void UpdateClickThrough()
    {
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        EnsureLayered();

        // 按住鼠标期间保持交互，避免拖拽中途穿透翻转把拖拽交给下层窗口
        // （中键拖模型时模型可能被拖离光标，IsPointerOverModel 会变 false）
        var held = (GetAsyncKeyState(VkLbutton) & 0x8000) != 0
                || (GetAsyncKeyState(VkRbutton) & 0x8000) != 0
                || (GetAsyncKeyState(VkMbutton) & 0x8000) != 0;
        var want = !_control.IsPointerOverModel && !held;
        if (want == _clickThrough) return;
        _clickThrough = want;

        var style = GetWindowLongPtr(hwnd, GwlExstyle).ToInt64();
        style = want ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLongPtr(hwnd, GwlExstyle, (IntPtr)style);
    }

    /// <summary>托盘菜单：显示 / 隐藏切换。</summary>
    public void ToggleVisible()
    {
        if (IsVisible) Hide();
        else { Show(); Activate(); }
    }

    /// <summary>托盘菜单：重置模型大小与位置。</summary>
    public void ResetModel()
    {
        _control.ResetModel();
    }

    /// <summary>自动表情联动：转发给控制层切换/清除表情。</summary>
    public void SetExpression(string? name)
    {
        _control.SetExpression(name);
    }

    /// <summary>托盘菜单：目光跟随开关（关闭时视线回正并停止跟随光标）。</summary>
    public void SetGazeFollow(bool enabled)
    {
        _gazeFollow = enabled;
        _control.SetGazeFollow(enabled);
    }

    /// <summary>托盘菜单：帧率显示开关。</summary>
    public void SetFpsVisible(bool visible)
    {
        _fpsBadge.IsVisible = visible;
    }

    /// <summary>手柄参数驱动开关：转发给渲染层（关闭时参数自动回中）。</summary>
    public void SetGamepadEnabled(bool enabled)
    {
        _control.SetGamepadEnabled(enabled);
    }

    /// <inheritdoc />
    public void SetDisplayPower(bool on)
    {
        if (_displayOn == on) return;
        _displayOn = on;
        Logger.Information("display power render={On}", on);
        ControlRenderLoop();
    }

    /// <summary>按"窗口可见 且 显示器亮"决定渲染链启停：关屏/隐藏都停，两者都恢复才拉起。</summary>
    private void ControlRenderLoop() => _control.SetRenderLoopEnabled(IsVisible && _displayOn);

    /// <summary>帧率统计：取走渲染层这一秒渲染的帧数，刷新显示。</summary>
    private void UpdateFps()
    {
        if (!_fpsBadge.IsVisible) return;
        _fpsAccum += _control.TakeRenderedFrames();
        var now = DateTime.Now;
        var elapsed = (now - _fpsSampleTime).TotalSeconds;
        if (elapsed < 1) return;
        _fpsSampleTime = now;
        _fpsText.Text = $"{Math.Round(_fpsAccum / elapsed)}";
        _fpsAccum = 0;
    }

    /// <summary>托盘菜单：退出（原生窗口不劫持关闭行为）。</summary>
    public void ExitForShutdown()
    {
        Close();
    }
}
