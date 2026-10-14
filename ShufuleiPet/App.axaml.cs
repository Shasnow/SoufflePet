using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
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
            // --native：用 Live2DCSharpSDK OpenGL 原生渲染（对比 WebView 方案）
            var useNative = desktop.Args?.Contains("--native") == true;
            Logger.Information("Lifetime initialized native={Native}", useNative);

            // ===== 组合根：注册服务 =====
            var services = new ServiceCollection();
            services.AddSingleton(AutoExpressionConfig.Load());
            services.AddSingleton<IProcessDetector, ProcessDetector>();
            services.AddSingleton<IForegroundDetector, ForegroundDetector>();
            services.AddSingleton<IAutoExpressionService, AutoExpressionService>();
            services.AddSingleton<ILocalFileServer, LocalFileServer>();
            services.AddSingleton<ILipSyncService, LipSyncService>();
            services.AddTransient<MainWindow>();
            services.AddTransient<NativePetWindow>();
            // 按启动参数二选一，确保窗口与 petOps 是同一实例
            services.AddSingleton<IPetWindow>(sp => useNative
                ? sp.GetRequiredService<NativePetWindow>()
                : sp.GetRequiredService<MainWindow>());
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
            autoExpression.ExpressionChanged += petOps.SetAutoExpression;

            // music 状态进出 → 口型律动采集启停（停用时不占音频设备）
            var lipSync = _provider.GetRequiredService<ILipSyncService>();
            autoExpression.MusicStateChanged += active => lipSync.Active = active;

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
