// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using SoufflePet.Core.Chat;
using SoufflePet.Views.Web;

namespace SoufflePet.Services;

/// <summary>React 主窗口的呼出服务（DI 注册）：单例窗口 + 全局热键 + 桌宠交互唤醒。</summary>
public interface IAppWindowService
{
    /// <summary>
    /// 在桌宠窗口上注册全局热键（Ctrl+Alt+Space）并把热键回调挂进其 WndProc 钩子链。
    /// 须在桌宠窗口 Show() 之后调用——依赖已创建的 HWND；重复调用无效果。
    /// </summary>
    void Start(TopLevel petWindow);

    /// <summary>
    /// 切换主窗口显隐；首次打开时停靠在 anchor（桌宠窗口）旁边。
    /// fromPet 表示由点击桌宠触发：此时若窗口可见但被压在后台，应先提到前台而不是收起。
    /// </summary>
    void Toggle(Window anchor, bool fromPet = false);

    /// <summary>
    /// 戳一戳唤醒：让桌宠发起一轮自主对话（反应动作由桌宠窗口先播），
    /// 并确保主窗口可见，以便用户看到桌宠的回应。
    /// </summary>
    void WakeByPoke(Window anchor);

    /// <summary>摸摸头唤醒：同戳一戳，发起一轮自主对话并确保主窗口可见。</summary>
    void WakeByHeadPat(Window anchor);
}

/// <summary>
/// React 主窗口服务：首次呼出时创建 <see cref="AppWebWindow" /> 并复用（关闭只隐藏）。
/// 热键实现与 PowerNotificationService 一致——桌宠窗口的 WndProc 由 Avalonia 接管，
/// 用 Win32Properties.AddWndProcHookCallback 挂进钩子链拦截 WM_HOTKEY，
/// 因此直接复用桌宠窗口句柄注册热键，无需自建消息窗口。
/// </summary>
public sealed class AppWindowService : IAppWindowService, IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<AppWindowService>();

    /// <summary>
    /// 运行统计：戳一戳次数在这里累计——桌宠窗口只有原生一个版本，但单击 / 右键菜单 /
    /// 热键都会走到本服务，是「用户戳了桌宠一下」的唯一入口，统计项不会漏记。
    /// </summary>
    private readonly IStatisticsService _statistics;
    private readonly ChatTriggerRouter _triggers;
    private readonly IServiceProvider _provider;

    /// <summary>React SPA 窗口（延迟创建：首次呼出前不初始化 WebView2）。</summary>
    private AppWebWindow? _window;
    private bool _failed;

    public AppWindowService(
        IStatisticsService statistics,
        ChatTriggerRouter triggers,
        IServiceProvider provider)
    {
        _statistics = statistics;
        _triggers = triggers;
        _provider = provider;
    }

    /// <summary>热键 ID（窗口内唯一即可，取值避开系统保留范围）。</summary>
    private const int HotkeyId = 0x5C41;

    private const uint WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const int VkSpace = 0x20;

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
            Logger.Warning("app window hotkey skipped: pet window handle not created yet");
            return;
        }

        _hotkeyHost = petWindow;
        _hook = HookWndProc;
        Win32Properties.AddWndProcHookCallback(petWindow, _hook);

        // MOD_NOREPEAT：按住不放时只触发一次，避免窗口反复开关
        _registered = RegisterHotKey(hwnd, HotkeyId, ModControl | ModAlt | ModNoRepeat, VkSpace);
        if (_registered) Logger.Information("app window hotkey registered Ctrl+Alt+Space");
        else Logger.Warning("app window hotkey register failed err={Error}", Marshal.GetLastWin32Error());
    }

    public void Toggle(Window anchor, bool fromPet = false)
    {
        if (_disposed || GetWindow() is not { } window) return;

        // 最小化窗口一律还原并提到前台，不管是热键/托盘还是点桌宠触发——
        // 用户想"打开"一个最小化的窗口，而不是把它隐藏到托盘。
        if (window.WindowState == WindowState.Minimized)
        {
            window.ShowNear(anchor);
            return;
        }

        // 点桌宠时若主窗口可见但被压在后台（上次失焦是被别的应用抢走），
        // 应该先把它提到前台，而不是直接收起——否则点一下只会在隐藏/后台之间来回切。
        if (fromPet && window.IsVisible && !window.WasForeground)
        {
            window.ShowNear(anchor);
            return;
        }

        if (window.IsVisible) window.HideAndSave();
        else window.ShowNear(anchor);
    }

    /// <summary>戳一戳：发起一轮自主对话，并保证主窗口显示在桌宠旁边（已显示则仅激活）。</summary>
    public void WakeByPoke(Window anchor)
    {
        if (_disposed) return;
        _statistics.AddPoke();

        if (GetWindow() is { } window)
        {
            window.ShowNear(anchor);
            _triggers.WakeByPoke();
        }
    }

    /// <summary>摸摸头：同戳一戳，发起一轮自主对话并保证主窗口可见。</summary>
    public void WakeByHeadPat(Window anchor)
    {
        if (_disposed) return;
        _statistics.AddHeadPat();

        if (GetWindow() is { } window)
        {
            window.ShowNear(anchor);
            _triggers.WakeByHeadPat();
        }
    }

    /// <summary>
    /// 延迟创建 React 窗口：首次呼出前不初始化 WebView2。
    /// 初始化失败只记录错误并提示一次，不再回退旧窗口（旧 Avalonia UI 已在 M7 删除）。
    /// </summary>
    private AppWebWindow? GetWindow()
    {
        if (_window is not null) return _window;
        if (_failed) return null;

        try
        {
            _window = ActivatorUtilities.CreateInstance<AppWebWindow>(_provider);
            return _window;
        }
        catch (Exception ex)
        {
            _failed = true;
            Logger.Error(ex, "create react app window failed; WebView2 runtime may be missing");
            ShowStartupError(ex);
            return null;
        }
    }

    /// <summary>
    /// 用系统消息框提示一次：React 窗口是唯一 UI，创建失败时用户没有其它入口可看日志。
    /// 只在失败路径调用，不影响正常启动。
    /// </summary>
    private static void ShowStartupError(Exception ex)
    {
        const uint MbIconError = 0x10;
        var message =
            "无法打开 SoufflePet 界面：WebView2 初始化失败。\n\n" +
            "请确认已安装 Microsoft Edge WebView2 Runtime 后重试。\n" +
            "详细信息已写入日志。\n\n" +
            ex.Message;
        MessageBoxW(IntPtr.Zero, message, "SoufflePet", MbIconError);
    }

    /// <summary>热键回调（UI 线程消息泵中触发）：按桌宠窗口当前位置停靠呼出。</summary>
    private IntPtr HookWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId && !_disposed && _hotkeyHost is Window host)
            Toggle(host);
        return IntPtr.Zero;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
