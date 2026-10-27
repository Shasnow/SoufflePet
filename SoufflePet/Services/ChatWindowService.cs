// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Serilog;
using SoufflePet.ViewModels;
using SoufflePet.Views;

namespace SoufflePet.Services;

/// <summary>LLM 对话窗口的呼出服务（DI 注册）：单例窗口 + 全局热键。</summary>
public interface IChatWindowService
{
    /// <summary>
    /// 在桌宠窗口上注册全局热键（Ctrl+Alt+Space）并把热键回调挂进其 WndProc 钩子链。
    /// 须在桌宠窗口 Show() 之后调用——依赖已创建的 HWND；重复调用无效果。
    /// </summary>
    void Start(TopLevel petWindow);

    /// <summary>切换对话窗口显隐；打开时停靠在 anchor（桌宠窗口）旁边。</summary>
    void Toggle(Window anchor);

    /// <summary>
    /// 戳一戳唤醒：让桌宠发起一轮自主对话（先由窗口侧播放反应动作），
    /// 并确保对话窗口可见，以便用户看到桌宠的回应。
    /// </summary>
    void WakeByPoke(Window anchor);
}

/// <summary>
/// 对话窗口服务：首次呼出时创建 ChatWindow 并复用（关闭只隐藏）。
/// 热键实现与 PowerNotificationService 一致——桌宠窗口的 WndProc 由 Avalonia 接管，
/// 用 Win32Properties.AddWndProcHookCallback 挂进钩子链拦截 WM_HOTKEY，
/// 因此直接复用桌宠窗口句柄注册热键，无需自建消息窗口。
/// </summary>
public sealed class ChatWindowService : IChatWindowService, IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<ChatWindowService>();

    /// <summary>对话视图模型（DI 单例）：窗口隐藏后再次呼出仍保留对话内容。</summary>
    private readonly ChatViewModel _viewModel;

    /// <summary>
    /// 运行统计：戳一戳次数在这里累计——桌宠两个渲染版本（原生 / WebView）的戳一戳都会调到本服务，
    /// 是「用户戳了桌宠一下」的唯一入口，统计项不会因版本不同而漏记。
    /// </summary>
    private readonly IStatisticsService _statistics;

    public ChatWindowService(ChatViewModel viewModel, IStatisticsService statistics)
    {
        _viewModel = viewModel;
        _statistics = statistics;
    }

    /// <summary>热键 ID（窗口内唯一即可，取值避开系统保留范围）。</summary>
    private const int HotkeyId = 0x5C41;

    private const uint WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const int VkSpace = 0x20;

    /// <summary>对话窗口与桌宠窗口之间的间距（DIP）。</summary>
    private const double GapDip = 12;

    private ChatWindow? _window;
    private TopLevel? _hotkeyHost;
    private Win32Properties.CustomWndProcHookCallback? _hook;
    private bool _registered;
    private bool _disposed;

    public void Start(TopLevel petWindow)
    {
        if (_registered || _disposed) return;

        var hwnd = petWindow.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
        {
            Logger.Warning("chat hotkey skipped: pet window handle not created yet");
            return;
        }

        _hotkeyHost = petWindow;
        _hook = HookWndProc;
        Win32Properties.AddWndProcHookCallback(petWindow, _hook);

        // MOD_NOREPEAT：按住不放时只触发一次，避免窗口反复开关
        _registered = RegisterHotKey(hwnd, HotkeyId, ModControl | ModAlt | ModNoRepeat, VkSpace);
        if (_registered) Logger.Information("chat hotkey registered Ctrl+Alt+Space");
        else Logger.Warning("chat hotkey register failed err={Error}", Marshal.GetLastWin32Error());
    }

    public void Toggle(Window anchor)
    {
        if (_disposed) return;
        var window = _window ??= new ChatWindow { DataContext = _viewModel };
        if (window.IsVisible)
        {
            window.Hide();
            return;
        }

        ShowWindow(window, anchor);
    }

    /// <summary>戳一戳：发起一轮自主对话，并保证对话窗口显示在桌宠旁边（已显示则仅激活）。</summary>
    public void WakeByPoke(Window anchor)
    {
        if (_disposed) return;
        _statistics.AddPoke();
        _viewModel.WakeByPoke();

        var window = _window ??= new ChatWindow { DataContext = _viewModel };
        if (window.IsVisible)
        {
            window.Activate();
            window.FocusInput();
            return;
        }

        ShowWindow(window, anchor);
    }

    /// <summary>停靠到 anchor 旁边并显示对话窗口。</summary>
    private static void ShowWindow(ChatWindow window, Window anchor)
    {
        PositionNear(window, anchor);
        window.Show();
        window.Activate();
        window.FocusInput();
    }

    /// <summary>热键回调（UI 线程消息泵中触发）：按桌宠窗口当前位置停靠呼出。</summary>
    private IntPtr HookWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId && !_disposed && _hotkeyHost is Window host)
            Toggle(host);
        return IntPtr.Zero;
    }

    /// <summary>
    /// 停靠定位：优先放在桌宠窗口左侧（右侧放不下时换到右侧），底边对齐；
    /// 尺寸从 DIP 换算成物理像素后按工作区夹紧。
    /// </summary>
    private static void PositionNear(Window window, Window anchor)
    {
        var screen = anchor.Screens.ScreenFromWindow(anchor) ?? anchor.Screens.Primary;
        if (screen is null) return;

        var wa = screen.WorkingArea;
        var scaling = anchor.RenderScaling > 0 ? anchor.RenderScaling : 1.0;
        // 窗口可被拖拽改尺寸，Bounds 才是当前实际大小；尚未量出布局时回退到 XAML 里的初始值
        var widthDip = window.Bounds.Width > 0 ? window.Bounds.Width : window.Width;
        var heightDip = window.Bounds.Height > 0 ? window.Bounds.Height : window.Height;
        var w = (int)Math.Round(widthDip * scaling);
        var h = (int)Math.Round(heightDip * scaling);
        var gap = (int)Math.Round(GapDip * scaling);
        var anchorW = (int)Math.Round(anchor.Bounds.Width * scaling);
        var anchorH = (int)Math.Round(anchor.Bounds.Height * scaling);

        // 默认贴桌宠窗口左缘；左侧超出工作区则改贴右缘
        var x = anchor.Position.X - w - gap;
        if (x < wa.X) x = anchor.Position.X + anchorW + gap;

        // 夹紧时 upper 不能小于 lower，否则 Math.Clamp 会抛异常（窗口比工作区还大时）
        var maxX = Math.Max(wa.X, wa.Right - w);
        var maxY = Math.Max(wa.Y, wa.Bottom - h);
        window.Position = new PixelPoint(
            Math.Clamp(x, wa.X, maxX),
            Math.Clamp(anchor.Position.Y + anchorH - h, wa.Y, maxY));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_registered)
        {
            var hwnd = _hotkeyHost?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero) UnregisterHotKey(hwnd, HotkeyId);
            _registered = false;
        }

        if (_hotkeyHost != null && _hook != null)
        {
            // 摘钩子是纯托管列表操作，窗口此时即使已销毁也安全
            Win32Properties.RemoveWndProcHookCallback(_hotkeyHost, _hook);
            _hook = null;
            _hotkeyHost = null;
        }

        _window?.CloseForShutdown();
        _window = null;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, int vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}