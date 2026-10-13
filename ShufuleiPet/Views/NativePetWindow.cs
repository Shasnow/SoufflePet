using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Serilog;
using ShufuleiPet.Views.Native;

namespace ShufuleiPet.Views;

/// <summary>桌宠窗口的托盘菜单操作接口（WebView 版与原生版共用）。</summary>
public interface IPetWindow
{
    void ToggleVisible();
    void ExitForShutdown();
}

/// <summary>原生渲染版桌宠窗口：Live2DCSharpSDK + OpenGL，不依赖 WebView。</summary>
public class NativePetWindow : Window, IPetWindow
{
    private static readonly ILogger Logger = Log.ForContext<NativePetWindow>();

    private readonly NativePetControl _control;
    private readonly DispatcherTimer _renderTimer;
    private DateTime _lastMoveLog;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private const uint WmNcLButtonDown = 0x00A1;
    private static readonly IntPtr HtCaption = new(2);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint lpPoint);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint lpPoint);

    private const int GwlExstyle = -20;
    private const long WsExTransparent = 0x00000020;
    private const long WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x2;
    private const int VkLbutton = 0x01;
    private const int VkRbutton = 0x02;
    private bool _clickThrough;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    public NativePetWindow()
    {
        Title = "舒芙蕾桌宠（原生）";
        Width = 480;
        Height = 680;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        WindowDecorations = WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _control = new NativePetControl();
        Content = _control;

        // 60 FPS 驱动 GL 重绘 + 全局鼠标追踪
        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _renderTimer.Tick += (_, _) =>
        {
            _control.RequestNextFrameRendering();
            TrackGlobalCursor();
            UpdateClickThrough();
        };
        _renderTimer.Start();

        // 交互：
        // - 按住拖动（位移 > 6px）→ 系统级拖拽窗口
        // - 左键单击 → 随机切换表情
        // - 右键单击 → 重播 Idle 动作
        // - 滚轮 → 缩放模型
        Point? pressPoint = null;
        PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                pressPoint = e.GetPosition(this);
        };
        PointerMoved += (_, e) =>
        {
            // 目光跟随由 TrackGlobalCursor 全局轮询负责（PointerMoved 只在窗口内触发），
            // 这里只处理按住拖动判定。
            var pos = e.GetPosition(this);

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
            if (pressPoint is null) return;
            pressPoint = null;
            if (e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.RightButtonReleased)
                _control.ReplayMotion();
            else
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

            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen is null) return;
            var wa = screen.WorkingArea;
            Position = new PixelPoint(wa.Right - (int)Bounds.Width - 48, wa.Bottom - (int)Bounds.Height - 24);
        };
    }

    /// <summary>
    /// 全局鼠标追踪：Avalonia 的 PointerMoved 只在光标位于窗口内时触发，移出窗口后
    /// 视线会定住。这里用 GetCursorPos 轮询屏幕光标（ClientToScreen 换算窗口客户区
    /// 原点），光标在窗口外也持续驱动目光跟随，超出窗口的坐标夹到 [-1,1]。
    /// </summary>
    private void TrackGlobalCursor()
    {
        if (!IsVisible || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        if (!GetCursorPos(out var cursor)) return;
        var origin = new NativePoint();
        if (!ClientToScreen(hwnd, ref origin)) return;

        // GetCursorPos 是物理像素，Bounds 是 DIP，先按 RenderScaling 换算
        var scaling = RenderScaling <= 0 ? 1.0 : RenderScaling;
        var x = (float)((cursor.X - origin.X) / scaling / Bounds.Width * 2 - 1);
        var y = (float)((cursor.Y - origin.Y) / scaling / Bounds.Height * 2 - 1);

        // 视图坐标：中心原点、y 向上；窗口外光标夹到边界，视线始终朝向鼠标
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
        var held = (GetAsyncKeyState(VkLbutton) & 0x8000) != 0
                || (GetAsyncKeyState(VkRbutton) & 0x8000) != 0;
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

    /// <summary>托盘菜单：退出（原生窗口不劫持关闭行为）。</summary>
    public void ExitForShutdown()
    {
        Close();
    }
}
