using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using ShufuleiPet.Models;
using ShufuleiPet.Services;
using ShufuleiPet.Views;

namespace ShufuleiPet;

public partial class App : Application
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

            // 手柄联动：命中游戏（切到"手柄"表情）时开始把手柄/键鼠输入写进模型摇杆参数；
            // 手动模式下不看检测结果，一直驱动（方便没打游戏时调试/演示）。
            autoExpression.ExpressionChanged += name =>
            {
                if (settings.GamepadManual) return; // 手动模式：表情由托盘控制，忽略自动切换
                petOps.SetAutoExpression(name);
                petOps.SetGamepadEnabled(settings.GamepadEnabled && name == settings.AutoExpressionGameExpression);
            };

            // music 状态进出 → 口型律动采集启停（停用时不占音频设备）
            var lipSync = _provider.GetRequiredService<ILipSyncService>();
            autoExpression.MusicStateChanged += active => lipSync.Active = active;

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
                }
            };

            // 手柄驱动开关重算：总开关开 且（手动模式 或 当前正处于"手柄"表情）
            void ApplyGamepadDriving() =>
                petOps.SetGamepadEnabled(settings.GamepadEnabled
                    && (settings.GamepadManual || autoExpression.CurrentExpression == settings.AutoExpressionGameExpression));

            // 常显手柄切换：进入手动模式直接显示手柄表情；退出则清掉手动表情交回自动检测
            void ApplyGamepadManual()
            {
                if (settings.GamepadManual)
                {
                    petOps.SetAutoExpression(settings.AutoExpressionGameExpression);
                }
                else
                {
                    petOps.SetAutoExpression(null);
                    autoExpression.PollNow(); // 立刻轮询一次（命中游戏会马上切回来）
                }
                ApplyGamepadDriving();
            }

            // 应用持久化的初始状态（订阅只响应变化，初始值需显式应用一次）
            petOps.SetGazeFollow(settings.GazeFollowEnabled);
            petOps.SetFpsVisible(settings.IsShowFps);
            if (settings.GamepadManual) ApplyGamepadManual();

            try
            {
                window.Show();
                Logger.Information("Window shown visible={Visible} hwnd={Hwnd}", window.IsVisible, window.TryGetPlatformHandle()?.Handle);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "window.Show() failed");
            }

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
