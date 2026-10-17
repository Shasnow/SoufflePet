using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Serilog;

namespace ShufuleiPet.Services;

/// <summary>显示器电源状态通知服务抽象（DI 注册用）。</summary>
public interface IPowerNotificationService : IDisposable
{
    /// <summary>显示器电源状态变化（true=打开，false=关闭）。在 UI 线程触发，订阅方直接操作窗口即可。</summary>
    event Action<bool>? DisplayPowerChanged;

    /// <summary>
    /// 启动监听：把回调挂进 window 的 WndProc 钩子链，并以该窗口句柄注册电源通知
    /// （重复调用无效果）。须在窗口 Show() 之后调用——依赖已创建的 HWND。
    /// </summary>
    void Start(TopLevel window);
}

/// <summary>
/// Windows 电源事件通知服务：注册 GUID_CONSOLE_DISPLAY_STATE（Win8+），在显示器
/// 关闭/打开/调光时收到 WM_POWERBROADCAST(PBT_POWERSETTINGCHANGE)。微软文档明确该
/// 通知面向"主动向显示设备渲染内容的应用"（如媒体可视化），显示器关闭时应停止
/// 渲染以减少能耗——桌宠此时无人观看。选它而非旧的 GUID_MONITOR_POWER_ON：Win8+
/// 的关屏/现代待机路径都会报，且数据是三态 MONITOR_DISPLAY_STATE（0=Off 1=On 2=Dim），
/// 能区分"调光"（仍显示画面，继续渲染）与"真正关闭"（停止渲染）。
/// 实现要点：通知投递到注册时指定的 HWND，而桌宠窗口的 WndProc 由 Avalonia 接管，
/// 应用层默认看不到未处理消息——用公开 API Win32Properties.AddWndProcHookCallback
/// 把回调挂进该窗口的 WndProc 钩子链即可拦截，因此直接复用桌宠窗口句柄注册，
/// 无需自建隐藏窗口与消息循环。钩子运行在 UI 线程消息泵中，事件天然在 UI 线程触发。
/// </summary>
public sealed class PowerNotificationService : IPowerNotificationService
{
    private static readonly ILogger Logger = Log.ForContext<PowerNotificationService>();

    /// <summary>GUID_CONSOLE_DISPLAY_STATE：当前显示器显示状态变化（Win8+，WinNT.h 官方 Power Setting GUIDs）。</summary>
    private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    /// <summary>MONITOR_DISPLAY_STATE 三态（wdm.h）：0=关闭，1=打开，2=调光（画面仍可见）。</summary>
    private const byte DisplayOff = 0;

    private const uint WmPowerBroadcast = 0x0218;
    private const int PbtPowerSettingChange = 0x8013;
    private const uint DeviceNotifyWindowHandle = 0;

    /// <summary>已挂接的窗口与回调实例（Dispose 时按同一实例摘除钩子、注销注册）。</summary>
    private TopLevel? _window;
    private Win32Properties.CustomWndProcHookCallback? _hook;

    private IntPtr _registration;
    private bool _started;
    private bool _disposed;

    public event Action<bool>? DisplayPowerChanged;

    public void Start(TopLevel window)
    {
        if (_started || _disposed) return;
        _started = true;
        try
        {
            var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
            {
                Logger.Warning("power notify start skipped: window handle not created yet");
                return;
            }

            _window = window;
            _hook = HookWndProc;
            // 挂进桌宠窗口的 WndProc 钩子链：拦截发到该窗口的 WM_POWERBROADCAST
            Win32Properties.AddWndProcHookCallback(window, _hook);

            var guid = ConsoleDisplayState; // ref 参数不能用 readonly 字段，拷到局部
            _registration = RegisterPowerSettingNotification(hwnd, ref guid, DeviceNotifyWindowHandle);
            if (_registration == IntPtr.Zero)
                Logger.Warning("power setting notification register failed err={Error}", Marshal.GetLastWin32Error());
            else
                Logger.Debug("power notification registered display state={Guid}", ConsoleDisplayState);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "power notification start failed");
        }
    }

    /// <summary>WndProc 钩子回调：解析 POWERBROADCAST_SETTING 并触发事件（运行在 UI 线程消息泵中）。</summary>
    private IntPtr HookWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmPowerBroadcast && wParam.ToInt64() == PbtPowerSettingChange && !_disposed)
        {
            try
            {
                // POWERBROADCAST_SETTING 布局：GUID PowerSetting(16) + DWORD DataLength(4) + UCHAR Data[]
                var guid = Marshal.PtrToStructure<Guid>(lParam);
                var dataLength = (uint)Marshal.ReadInt32(lParam, 16);
                if (guid == ConsoleDisplayState && dataLength >= 1)
                {
                    // 三态映射到"是否继续渲染"：Off → false 停渲染；
                    // On / Dim → true（Dim 仍在显示画面，照常渲染）
                    var state = Marshal.ReadByte(lParam, 20);
                    var on = state != DisplayOff;
                    Logger.Information("display power changed state={State} render={On}", state, on);
                    DisplayPowerChanged?.Invoke(on);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "handle power broadcast failed");
            }
        }
        // 不改 handled：Avalonia 也不处理该消息，放行走原默认处理，行为与未挂钩时一致
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_registration != IntPtr.Zero)
        {
            UnregisterPowerSettingNotification(_registration);
            _registration = IntPtr.Zero;
        }
        if (_window != null && _hook != null)
        {
            // 摘除钩子是纯托管列表操作，窗口此时即使已关闭（句柄销毁）也安全
            Win32Properties.RemoveWndProcHookCallback(_window, _hook);
            _window = null;
            _hook = null;
        }
    }

    // ==================== Win32 互操作 ====================

    [DllImport("user32.dll")]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid PowerSettingGuid, uint DeviceFlags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}
