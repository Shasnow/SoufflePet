using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using ShufuleiPet.Models;
using ShufuleiPet.Services;
using ShufuleiPet.Utils;
using ShufuleiPet.Views;

namespace ShufuleiPet;

public class App : Application
{
    private static readonly ILogger Logger = Log.ForContext<App>();
    public static double RenderScaling { get; private set; } = 1.0;

    /// <summary>DI 容器（组合根在 OnFrameworkInitializationCompleted 装配，Exit 时统一 Dispose）。</summary>
    private ServiceProvider? _provider;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Logger.Error(e.Exception, "Unhandled UI exception");
            e.Handled = true;
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 默认：Live2DCSharpSDK OpenGL 原生渲染；--webview：切 WebView 方案（对比用）
            var useWebView = desktop.Args?.Contains("--webview") == true;
            Logger.Information("Lifetime initialized native={Native}", !useWebView);

            // ===== 组合根：注册服务 =====
            var services = new ServiceCollection();
            services.AddSingleton(ShufuleiSettings.Load());
            services.AddSingleton<IProcessDetector, ProcessDetector>();
            services.AddSingleton<IForegroundDetector, ForegroundDetector>();
            services.AddSingleton<IAutoExpressionService, AutoExpressionService>();
            services.AddSingleton<ILocalFileServer, LocalFileServer>();
            services.AddSingleton<ILipSyncService, LipSyncService>();
            services.AddSingleton<IGamepadService, GamepadService>();
            services.AddSingleton<IPowerNotificationService, PowerNotificationService>();
            services.AddTransient<WebViewPetWindow>();
            services.AddTransient<NativePetWindow>();
            // 按启动参数二选一，确保窗口与 petOps 是同一实例
            services.AddSingleton<IPetWindow>(sp => useWebView
                ? sp.GetRequiredService<WebViewPetWindow>()
                : sp.GetRequiredService<NativePetWindow>());
            services.AddSingleton<ITrayService, TrayService>();
            _provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,   // 构建时即校验漏注册/循环依赖
                ValidateScopes = true,
            });

            // ===== 解析与接线 =====
            var petOps = _provider.GetRequiredService<IPetWindow>();
            var window = (Window)petOps; // 单实例，避免二次解析
            desktop.MainWindow = window;
            RenderScaling = window.RenderScaling;
            Logger.Debug("Main window assigned");

            // 自动表情事件 → 窗口（订阅先于 DispatcherTimer 触发，无竞态）
            var autoExpression = _provider.GetRequiredService<IAutoExpressionService>();
            var settings = _provider.GetRequiredService<ShufuleiSettings>();
            var lipSync = _provider.GetRequiredService<ILipSyncService>();
            var gamepad = _provider.GetRequiredService<IGamepadService>();

            // 手柄联动：命中游戏（切到"手柄"表情）时开始把手柄/键鼠输入写进模型摇杆参数；
            // 手动模式下不看检测结果，一直驱动（方便没打游戏时调试/演示）。
            autoExpression.ExpressionChanged += name =>
            {
                if (settings.GamepadManual) return; // 手动模式：表情由托盘控制，忽略自动切换
                if (settings.MusicManual) return;  // 手动音乐模式：表情由托盘控制，忽略自动切换
                petOps.SetExpression(name);
                ApplyGamepadDriving();
                ApplyLipSync();
            };

            // ===== 设置变化统一接线 =====
            // ShufuleiSettings 是唯一事实源：任何写入方（托盘菜单/设置界面）只改属性，
            // 持久化与运行时副作用全部在这一个订阅里处理，写入方不做 Save、不直接调服务。
            settings.PropertyChanged += (_, e) =>
            {
                try { settings.Save(); } catch (Exception ex) { Logger.Warning(ex, "save settings failed"); }
                switch (e.PropertyName)
                {
                    case nameof(ShufuleiSettings.GazeFollowEnabled):
                        petOps.SetGazeFollow(settings.GazeFollowEnabled);
                        break;
                    case nameof(ShufuleiSettings.IsShowFps):
                        petOps.SetFpsVisible(settings.IsShowFps);
                        break;
                    case nameof(ShufuleiSettings.GamepadEnabled):
                        ApplyGamepadDriving();
                        break;
                    case nameof(ShufuleiSettings.GamepadManual):
                        ApplyGamepadManual();
                        break;
                    case nameof(ShufuleiSettings.MusicManual):
                        ApplyMusicManual();
                        break;
                    case nameof(ShufuleiSettings.StartWithWindows):
                        ApplyAutoStart();
                        break;
                }
            };

            // 开机自启：把设置写进/移出 HKCU Run 注册表项（失败只记警告，不影响应用运行）
            void ApplyAutoStart()
            {
                try
                {
                    AutoStart.SetEnabled(settings.StartWithWindows);
                    Logger.Debug("auto start applied enabled={Enabled}", settings.StartWithWindows);
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "apply auto start failed");
                }
            }

            // 手柄驱动开关重算：总开关开 且（手动模式 或 当前正处于"手柄"表情）。
            // 同一路 driving 信号同时驱动窗口侧参数写入与服务侧轮询（gamepad.Start/Stop）——
            // 不驱动时停掉 30Hz 轮询，不白查 XInput/键鼠/光标
            void ApplyGamepadDriving()
            {
                var driving = settings.GamepadEnabled
                    && (settings.GamepadManual || autoExpression.CurrentExpression == settings.AutoExpressionGameExpression);
                petOps.SetGamepadEnabled(driving);
                if (driving)
                {
                    gamepad.Start();
                    lipSync.Stop(); // 手柄模式下不采集口型律动，避免干扰
                }
                else gamepad.Stop();
            }

            // 口型律动启停：music 检测状态或手动音乐模式 → 开始/停止采集
            // （是否真正采集还受 LipSyncEnabled 开关控制，见 LipSyncService 内部）
            void ApplyLipSync()
            {
                var on = settings.LipSyncEnabled
                         && (autoExpression.CurrentExpression == settings.AutoExpressionMusicExpression
                             || settings.MusicManual);
                if (on)
                {
                    lipSync.Start();
                    gamepad.Stop(); // 音乐模式下不采集手柄/键鼠，避免干扰
                }
                else lipSync.Stop();
            }

            // 常显手柄切换：进入手动模式直接显示手柄表情；退出则清掉手动表情交回自动检测
            void ApplyGamepadManual()
            {
                if (settings.GamepadManual)
                {
                    petOps.SetExpression(settings.AutoExpressionGameExpression);
                }
                else
                {
                    // 先恢复自动检测当前命中的表情（如 music 命中时的"麦克风"）：
                    // 手动模式期间检测状态没变，PollNow 内部会因状态去重直接返回、不会重发
                    // ExpressionChanged，只调 PollNow 的话表情会一直卡在默认（null）
                    petOps.SetExpression(autoExpression.CurrentExpression);
                    if (autoExpression.CurrentExpression == settings.AutoExpressionMusicExpression) ApplyLipSync();
                }
                ApplyGamepadDriving();
            }

            // 常显音乐模式切换：进入直接显示音乐表情并开始口型律动；退出则清掉手动表情交回自动检测
            void ApplyMusicManual()
            {
                if (settings.MusicManual)
                {
                    petOps.SetExpression(settings.AutoExpressionMusicExpression);
                }
                else
                {
                    // 先恢复自动检测当前命中的表情（如 game 命中时的"手柄"）
                    petOps.SetExpression(autoExpression.CurrentExpression);
                    if (autoExpression.CurrentExpression == settings.AutoExpressionGameExpression) ApplyGamepadDriving();
                }
                ApplyLipSync();
            }

            // 应用持久化的初始状态（订阅只响应变化，初始值需显式应用一次）
            petOps.SetGazeFollow(settings.GazeFollowEnabled);
            petOps.SetFpsVisible(settings.IsShowFps);
            if (settings.GamepadManual) ApplyGamepadManual();
            if (settings.MusicManual) ApplyMusicManual();
            ApplyAutoStart(); // 开机自启：启动时按设置重写一次注册表项（exe 移动后自动刷新路径）

            try
            {
                window.Show();
                Logger.Information("Window shown visible={Visible} hwnd={Hwnd}", window.IsVisible, window.TryGetPlatformHandle()?.Handle);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "window.Show() failed");
            }

            // 显示器电源事件：复用桌宠窗口句柄注册，把回调挂进该窗口的 WndProc 钩子链。
            // 必须在 Show() 之后启动——依赖已创建的 HWND；事件在 UI 线程消息泵中触发，
            // 直接调用即可。关屏时无人观看，停掉渲染链省 CPU/GPU（微软 Power Setting
            // GUIDs 文档的推荐用法）；亮屏恢复
            var power = _provider.GetRequiredService<IPowerNotificationService>();
            power.DisplayPowerChanged += petOps.SetDisplayPower;
            power.Start(window);

            _provider.GetRequiredService<ITrayService>().Show(this, desktop);

            // 退出时统一 Dispose 容器（收集 AutoExpressionService/LocalFileServer/TrayService 等 IDisposable）
            desktop.Exit += (_, _) =>
            {
                Logger.Information("Application exit, disposing service provider");
                _provider?.Dispose();
                _provider = null;
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
