// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Serilog;
using SoufflePet.Models;
using SoufflePet.Services;
using SoufflePet.Utils;

namespace SoufflePet.Views;

/// <summary>
/// WebView 版桌宠窗口：纯代码构建（无 XAML，与 NativePetWindow 风格一致），
/// 窗口内嵌 NativeWebView 加载本地静态服务器页面，负责光标转发、口型律动、
/// 拖窗、点击穿透、自动表情等宿主侧交互。
/// </summary>
public class WebViewPetWindow : Window, IPetWindow
{
    private static readonly ILogger Logger = Log.ForContext<WebViewPetWindow>();

    private readonly NativeWebView _webView;
    private bool _exiting;

    /// <summary>对话窗口服务：左键单击呼出 / 收起对话窗口、戳一戳后发起一轮对话。</summary>
    private readonly IChatWindowService _chat;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private const uint WmNcLButtonDown = 0x00A1; // WM_NCLBUTTONDOWN
    private static readonly IntPtr HtCaption = new(2); // HTCAPTION

    private readonly DispatcherTimer _cursorTimer;
    private int _lastCursorX = int.MinValue;
    private int _lastCursorY = int.MinValue;

    /// <summary>口型律动服务（设计时为 null）：每帧把音量推给页面。</summary>
    private readonly ILipSyncService? _lipSync;

    /// <summary>目光跟随开关（托盘勾选项）：推给页面，关闭时页面视线回正并停止 lookAt。
    /// 宿主侧仍照常转发光标（页面点击穿透的命中检测依赖同一份坐标）。</summary>
    private bool _gazeFollow = true;

    /// <summary>是否已完成首次定位（Opened 每次显示都会触发，位置只设一次）。</summary>
    private bool _positioned;

    /// <summary>显示器电源状态（false=屏已关）：推给页面停/启 ticker。</summary>
    private bool _displayOn = true;

    /// <summary>帧率显示开关（托盘勾选项）：推给页面控制右上角叠加层显隐。</summary>
    private bool _fpsVisible = true;

    /// <summary>上次推送给页面的口型音量（差值过小不推送，减少 InvokeScript 调用）。</summary>
    private float _lastPushedLip = float.MinValue;

    /// <summary>上次推送给页面的口型水平宽窄值。</summary>
    private float _lastPushedWide = float.MinValue;

    /// <summary>手柄输入服务（真实手柄优先，无手柄时 WASD + 鼠标回退）。</summary>
    private readonly IGamepadService? _gamepad;

    /// <summary>手柄参数驱动开关（托盘"手柄联动"）：推给页面，页面在 beforeModelUpdate 里写参数。</summary>
    private bool _gamepadDriving;

    /// <summary>上次推送给页面的手柄状态（含按键位掩码），用于脏检查，减少 InvokeScript 调用。</summary>
    private float _lastPadLx, _lastPadLy, _lastPadRx, _lastPadRy, _lastPadLt, _lastPadRt;
    private int _lastPadDx, _lastPadDy, _lastPadMask = -1;

    /// <summary>DI 构造：注入本地静态服务器（ILocalFileServer）、口型律动与手柄输入服务、对话窗口服务、设置。</summary>
    public WebViewPetWindow(SouffleSettings settings, ILocalFileServer? server, ILipSyncService? lipSync,
        IGamepadService? gamepad, IChatWindowService chat)
    {
        _lipSync = lipSync;
        _gamepad = gamepad;
        _chat = chat;

        Title = "SoufflePet（WebView）";
        Width = 480;
        Height = 680;
        MinWidth = 200;
        MinHeight = 200;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        WindowDecorations = WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _webView = new NativeWebView { Background = Brushes.Transparent };
        _webView.EnvironmentRequested += (_, e) => WebViewProfile.Apply(e);
        Content = _webView;

        if (server != null)
        {
            var port = server.Start();
            // 模型 URL 走查询参数传给页面：换模型只改设置（SoufflePet.json 的
            // ModelsDirectory / ModelName），页面脚本无需跟着改
            var modelUrl = $"{settings.ModelsDirectory}/{settings.ModelName}/{settings.ModelName}.model3.json";
            _webView.Source = new Uri($"http://localhost:{port}/index.html?model={Uri.EscapeDataString(modelUrl)}");
            Logger.Debug("webview model url={Url}", modelUrl);
        }

        // Opened 在每次 Hide 后再 Show 时都会触发，只在首次显示时定位到右下角，
        // 避免托盘切换显示/隐藏把用户拖动过的窗口位置复原
        Opened += (_, _) =>
        {
            if (_positioned) return;
            _positioned = true;
            MoveToBottomRight();
        };
        _webView.WebMessageReceived += OnWebMessage;

        // 全局鼠标追踪：页面里的 pointermove 只在光标位于窗口内时触发，移出窗口后
        // 视线会定住。这里由宿主轮询屏幕光标，换算成页面坐标后转发给页面。
        _cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _cursorTimer.Tick += (_, _) =>
        {
            if (!_displayOn) return; // 关屏期间不向页面转发输入/口型（页面 ticker 也已停）
            ForwardGlobalCursor();
            ForwardLipSync();
            ForwardGamepad();
        };
        _cursorTimer.Start();

        Closing += (_, e) =>
        {
            if (_exiting) return;
            e.Cancel = true;
            Hide(); // 关闭窗口 = 藏到托盘，托盘菜单才能真正退出
        };
    }

    /// <summary>托盘菜单：显示 / 隐藏切换。</summary>
    public void ToggleVisible()
    {
        if (IsVisible) Hide();
        else { Show(); Activate(); }
    }

    /// <summary>托盘菜单：重置模型大小与位置（转发给页面执行）。</summary>
    public void ResetModel()
    {
        try
        {
            _ = _webView.InvokeScript("window.onHostResetModel && window.onHostResetModel();");
        }
        catch
        {
            // 页面尚未加载完成时忽略
        }
    }

    /// <summary>自动表情联动：缓存状态后转发给页面；页面加载完成前先缓存，收到 ready 后重放。</summary>
    public void SetExpression(string? name)
    {
        _autoExpression = name;
        PushAutoExpression();
    }

    /// <summary>当前自动表情状态（页面模型异步加载完成前缓存，ready 后重放）。</summary>
    private string? _autoExpression;

    /// <summary>页面回报的当前实际表情名（含"戳一戳"随机表情的结果，null = 默认状态）。</summary>
    private volatile string? _currentExpression;

    /// <summary>当前实际表情：以页面回报为准，宿主缓存的自动表情状态可能已被"戳一戳"随机表情覆盖。</summary>
    public string? CurrentExpression => _currentExpression;

    /// <summary>把自动表情状态推给页面（页面未加载完成时 InvokeScript 会抛异常，忽略即可）。</summary>
    private void PushAutoExpression()
    {
        try
        {
            var script = _autoExpression == null
                ? "window.onHostResetExpression && window.onHostResetExpression();"
                : $"window.onHostSetExpression && window.onHostSetExpression('{_autoExpression}');";
            _ = _webView.InvokeScript(script);
        }
        catch
        {
            // 页面尚未加载完成时忽略，等 ready 消息重放
        }
    }

    /// <summary>托盘菜单：目光跟随开关（关闭时停止转发光标，页面收到后视线回正）。</summary>
    public void SetGazeFollow(bool enabled)
    {
        _gazeFollow = enabled;
        PushGazeFollow();
    }

    /// <summary>把目光跟随开关推给页面（页面未加载完成时忽略，等 ready 消息重放）。</summary>
    private void PushGazeFollow()
    {
        try
        {
            var v = _gazeFollow ? "true" : "false";
            _ = _webView.InvokeScript($"window.onHostSetGazeFollow && window.onHostSetGazeFollow({v});");
        }
        catch
        {
            // 页面尚未加载完成时忽略，等 ready 消息重放
        }
    }

    /// <summary>托盘菜单：帧率显示开关（推给页面控制右上角叠加层显隐）。</summary>
    public void SetFpsVisible(bool visible)
    {
        _fpsVisible = visible;
        PushFpsVisible();
    }

    /// <summary>把帧率显示开关推给页面（页面未加载完成时忽略，等 ready 消息重放）。</summary>
    private void PushFpsVisible()
    {
        try
        {
            var v = _fpsVisible ? "true" : "false";
            _ = _webView.InvokeScript($"window.onHostSetFpsVisible && window.onHostSetFpsVisible({v});");
        }
        catch
        {
            // 页面尚未加载完成时忽略，等 ready 消息重放
        }
    }

    /// <summary>托盘菜单：真正退出（解除 Closing 劫持）。</summary>
    public void ExitForShutdown()
    {
        _exiting = true;
        Close();
    }

    private void MoveToBottomRight()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var wa = screen.WorkingArea;
        // Position 是物理像素，Bounds 是 DIP，必须乘 RenderScaling（125% 屏 → 1.25）：
        // 否则按 DIP 尺寸算出的定位偏小，窗口底部/右侧会超出显示器
        var scaling = RenderScaling <= 0 ? 1.0 : RenderScaling;
        Position = new PixelPoint(
            wa.Right - (int)(Bounds.Width * scaling),
            wa.Bottom - (int)(Bounds.Height * scaling));
    }

    /// <summary>
    /// 全局鼠标追踪：GetCursorPos 轮询屏幕光标（物理像素），用 ClientToScreen 换算成
    /// 窗口客户区原点后再除以 RenderScaling 得到页面坐标（CSS 像素），
    /// 转发给页面驱动 model.focus，光标移出窗口后视线仍继续跟随。
    /// </summary>
    private void ForwardGlobalCursor()
    {
        if (!IsVisible || _exiting) return;
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (!GlobalCursor.TryGetClientPos(hwnd, out var px, out var py)) return;
        // 光标未变化时不打扰页面（InvokeScript 是跨进程调用，频率敏感）
        if (px == _lastCursorX && py == _lastCursorY) return;
        _lastCursorX = px;
        _lastCursorY = py;

        // 物理像素 → CSS 像素
        var scaling = RenderScaling <= 0 ? 1.0 : RenderScaling;
        var x = (px / scaling).ToString(CultureInfo.InvariantCulture);
        var y = (py / scaling).ToString(CultureInfo.InvariantCulture);
        try
        {
            // NativeWebView 没有 Post 消息 API，官方做法是 InvokeScript 执行页面 JS
            _ = _webView.InvokeScript($"window.onHostCursor && window.onHostCursor({x},{y});");
        }
        catch
        {
            // 页面尚未加载完成时忽略
        }
    }

    /// <summary>
    /// 口型律动：把音频音量与水平宽窄推给页面（音量差值 &gt;0.01 才推送，静音时不打扰页面）。
    /// 页面在 beforeModelUpdate 里把值写入 ParamMouthOpenY / ParamMouthForm2 驱动嘴部开合与横向扩张收缩。
    /// </summary>
    private void ForwardLipSync()
    {
        var lipSync = _lipSync;
        if (lipSync == null || !IsVisible || _exiting) return;
        var level = lipSync.CurrentLevel;
        var wide = lipSync.CurrentWideLevel;
        if (Math.Abs(level - _lastPushedLip) <= 0.01f && Math.Abs(wide - _lastPushedWide) <= 0.01f) return;
        _lastPushedLip = level;
        _lastPushedWide = wide;
        try
        {
            var v = level.ToString("0.###", CultureInfo.InvariantCulture);
            var w = wide.ToString("0.###", CultureInfo.InvariantCulture);
            _ = _webView.InvokeScript($"window.onHostSetLipSync && window.onHostSetLipSync({v},{w});");
        }
        catch
        {
            // 页面尚未加载完成时忽略
        }
    }

    /// <summary>
    /// 手柄联动：把手柄/键鼠输入推给页面（脏检查：连续量变化 &gt;0.01 或按键变化才推送，
    /// InvokeScript 是跨进程调用，频率敏感）。页面在 beforeModelUpdate 里把值写进
    /// 模型 ParamGroup3「手柄」的摇杆与按键参数，并平滑到目标值。
    /// </summary>
    private void ForwardGamepad()
    {
        var gamepad = _gamepad;
        if (gamepad == null || !_gamepadDriving || !IsVisible || _exiting) return;
        var s = gamepad.State;

        // 按键位掩码：与页面约定 A=1 B=2 X=4 Y=8 左肩=16 右肩=32 左摇杆按下=64 右摇杆按下=128
        var mask = (s.ButtonA ? 1 : 0) | (s.ButtonB ? 2 : 0) | (s.ButtonX ? 4 : 0) | (s.ButtonY ? 8 : 0)
                 | (s.LShoulder ? 16 : 0) | (s.RShoulder ? 32 : 0)
                 | (s.LStickButton ? 64 : 0) | (s.RStickButton ? 128 : 0);

        if (Math.Abs(s.LStickX - _lastPadLx) <= 0.01f
            && Math.Abs(s.LStickY - _lastPadLy) <= 0.01f
            && Math.Abs(s.RStickX - _lastPadRx) <= 0.01f
            && Math.Abs(s.RStickY - _lastPadRy) <= 0.01f
            && Math.Abs(s.LTrigger - _lastPadLt) <= 0.01f
            && Math.Abs(s.RTrigger - _lastPadRt) <= 0.01f
            && s.DPadX == _lastPadDx && s.DPadY == _lastPadDy && mask == _lastPadMask) return;

        _lastPadLx = s.LStickX;
        _lastPadLy = s.LStickY;
        _lastPadRx = s.RStickX;
        _lastPadRy = s.RStickY;
        _lastPadLt = s.LTrigger;
        _lastPadRt = s.RTrigger;
        _lastPadDx = s.DPadX;
        _lastPadDy = s.DPadY;
        _lastPadMask = mask;

        try
        {
            var args = string.Join(',',
                s.LStickX.ToString("0.###", CultureInfo.InvariantCulture),
                s.LStickY.ToString("0.###", CultureInfo.InvariantCulture),
                s.RStickX.ToString("0.###", CultureInfo.InvariantCulture),
                s.RStickY.ToString("0.###", CultureInfo.InvariantCulture),
                s.LTrigger.ToString("0.###", CultureInfo.InvariantCulture),
                s.RTrigger.ToString("0.###", CultureInfo.InvariantCulture),
                s.DPadX, s.DPadY, mask);
            _ = _webView.InvokeScript($"window.onHostSetGamepad && window.onHostSetGamepad({args});");
        }
        catch
        {
            // 页面尚未加载完成时忽略
        }
    }

    /// <summary>手柄参数驱动开关：转发给页面（关闭时页面把参数回中）。</summary>
    public void SetGamepadEnabled(bool enabled)
    {
        _gamepadDriving = enabled;
        PushGamepadEnabled();
    }

    /// <inheritdoc />
    public void SetDisplayPower(bool on)
    {
        if (_displayOn == on) return;
        _displayOn = on;
        Logger.Information("display power render={On}", on);
        PushDisplayPower();
    }

    /// <summary>把显示器电源状态推给页面（页面未加载完成时忽略，等 ready 消息重放）。</summary>
    private async void PushDisplayPower()
    {
        try
        {
            // await 拿到真实执行结果：fire-and-forget 会丢掉脚本执行异常，页面没生效也无从得知。
            // 布尔必须手动转小写：C# 插值生成 True/False，JS 里是未定义标识符直接抛错。
            // IIFE 返回诊断串区分三种情况：nofunc=页面未定义该函数（旧缓存/未加载）、
            // ok=已执行、err:=执行抛错
            var arg = _displayOn ? "false" : "true";
            var r = await _webView.InvokeScript(
                "(function(){ if (typeof window.onHostSetRenderPause !== 'function') return 'nofunc';" +
                $" try {{ window.onHostSetRenderPause({arg}); return 'ok'; }}" +
                " catch (e) { return 'err:' + e.message; } })()");
            Logger.Debug("render pause push result={Result}", r);
        }
        catch (Exception ex)
        {
            // 页面尚未加载完成时忽略，等 ready 消息重放
            Logger.Warning(ex, "render pause push failed");
        }
    }

    /// <summary>把驱动开关推给页面（页面未加载完成时忽略，等 ready 消息重放）。</summary>
    private void PushGamepadEnabled()
    {
        try
        {
            var v = _gamepadDriving ? "true" : "false";
            _ = _webView.InvokeScript($"window.onHostSetGamepadEnabled && window.onHostSetGamepadEnabled({v});");
        }
        catch
        {
            // 页面尚未加载完成时忽略，等 ready 消息重放
        }
    }

    // 网页里按住空白处/模型时调用 invokeCSharpAction('{"type":"drag"}')，
    // 这里转成系统级窗口拖拽（WM_NCLBUTTONDOWN + HTCAPTION），拖拽全程交给 Windows 处理
    private void OnWebMessage(object? sender, WebMessageReceivedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(e.Body)) return;
            Logger.Debug("web message body={Body}", e.Body);
            using var doc = JsonDocument.Parse(e.Body);
            if (!doc.RootElement.TryGetProperty("type", out var type)) return;
            var kind = type.GetString();
            if (kind == "ready")
            {
                // 页面模型加载完成，重放此前缓存的自动表情状态（解决启动竞态）
                PushAutoExpression();
                PushGazeFollow();
                PushFpsVisible();
                PushGamepadEnabled();
                PushDisplayPower();
                return;
            }
            if (kind == "renderpause")
            {
                // 页面回执：ticker 实际停/启状态（app/shared 分别是渲染与模型更新 ticker）
                if (!doc.RootElement.TryGetProperty("paused", out var paused)) return;
                var appStarted = doc.RootElement.TryGetProperty("app", out var a) && a.ValueKind == JsonValueKind.True;
                var sharedStarted = doc.RootElement.TryGetProperty("shared", out var s) && s.ValueKind == JsonValueKind.True;
                Logger.Information("page render pause applied={Paused} appTicker={App} sharedTicker={Shared}",
                    paused.ValueKind == JsonValueKind.True, appStarted, sharedStarted);
                return;
            }
            if (kind == "expression")
            {
                // 页面回报当前实际表情（自动联动、清除与"戳一戳"随机表情都会上报）
                _currentExpression = doc.RootElement.TryGetProperty("name", out var name)
                                     && name.ValueKind == JsonValueKind.String
                    ? name.GetString()
                    : null;
                return;
            }
            if (kind == "hit")
            {
                // 页面逐像素命中检测结果：光标不在模型可见像素上时挂
                // WS_EX_TRANSPARENT，让点击落到下层应用（与原生版 UpdateClickThrough 一致）
                if (!doc.RootElement.TryGetProperty("value", out var value)) return;
                var overModel = value.ValueKind == JsonValueKind.True;
                Dispatcher.Post(() => ApplyClickThrough(overModel));
                return;
            }
            if (kind == "chat")
            {
                // 左键单击桌宠：呼出 / 收起对话窗口（与原生版一致）
                Dispatcher.Post(() => _chat.Toggle(this));
                return;
            }
            if (kind == "poke")
            {
                // 戳一戳：页面已先播放反应动作/表情，这里发起一轮对话唤醒桌宠（先做反应再对话）
                Dispatcher.Post(() => _chat.WakeByPoke(this));
                return;
            }
            if (kind != "drag") return;
            Dispatcher.Post(() =>
            {
                var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (hwnd == IntPtr.Zero) return;
                ReleaseCapture();
                SendMessage(hwnd, WmNcLButtonDown, HtCaption, IntPtr.Zero);
            });
        }
        catch (JsonException)
        {
            // 忽略无法解析的消息
        }
    }

    private const int GwlExstyle = -20;
    private const long WsExTransparent = 0x00000020;
    private const long WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x2;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    /// <summary>当前是否处于点击穿透状态（避免重复调 SetWindowLongPtr）。</summary>
    private bool _clickThrough;

    /// <summary>
    /// 点击穿透：光标没压在模型可见像素上时挂 WS_EX_TRANSPARENT，点击落到下层应用。
    /// 命中状态由页面逐像素读 alpha 得出并经 web 消息上报。
    /// </summary>
    private void ApplyClickThrough(bool overModel)
    {
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;

        // WS_EX_TRANSPARENT 只对分层窗口生效，Avalonia 12 透明窗口默认不挂
        // WS_EX_LAYERED——首次切换前补挂上（不影响渲染）
        var style = GetWindowLongPtr(hwnd, GwlExstyle).ToInt64();
        if ((style & WsExLayered) == 0)
        {
            style |= WsExLayered;
            SetWindowLongPtr(hwnd, GwlExstyle, (IntPtr)style);
            SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);
        }

        var want = !overModel;
        if (want == _clickThrough) return;
        _clickThrough = want;
        style = want ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLongPtr(hwnd, GwlExstyle, (IntPtr)style);
    }
}
