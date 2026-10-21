// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using SoufflePet.Models;
using SoufflePet.Services;
using SoufflePet.Utils;
using SoufflePet.ViewModels;
using SoufflePet.Views;

namespace SoufflePet;

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
            services.AddSingleton(SouffleSettings.Load());
            services.AddSingleton<IProcessDetector, ProcessDetector>();
            services.AddSingleton<IForegroundDetector, ForegroundDetector>();
            services.AddSingleton<IAutoExpressionService, AutoExpressionService>();
            services.AddSingleton<ILocalFileServer, LocalFileServer>();
            services.AddSingleton<ILipSyncService, LipSyncService>();
            services.AddSingleton<IGamepadService, GamepadService>();
            services.AddSingleton<IPowerNotificationService, PowerNotificationService>();
            services.AddSingleton<ILlmChatService, LlmChatService>();
            services.AddSingleton<ChatViewModel>(); // 单例：隐藏后再呼出仍保留对话历史
            services.AddSingleton<IChatWindowService, ChatWindowService>();
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

            // 自动表情检测服务的事件订阅必须早于其内部 DispatcherTimer 首次触发，避免漏掉初始状态
            var autoExpression = _provider.GetRequiredService<IAutoExpressionService>();
            var settings = _provider.GetRequiredService<SouffleSettings>();
            var lipSync = _provider.GetRequiredService<ILipSyncService>();
            var gamepad = _provider.GetRequiredService<IGamepadService>();

            // ===== 表情结算漏斗 =====
            // autoExpression.EffectiveExpression 是"当前实际生效的表情"的唯一事实源：手动模式 > 自动检测
            // （game 优先于 music，与 AutoExpressionService 内部的汇总优先级一致）。
            // 三个下游（窗口表情、手柄轮询、口型采集）全部基于它判定，
            // 互斥天然成立，不再需要两侧服务互相 Stop 抢占。

            // 自动检测结果变化 → 走漏斗重算。手动模式接管期间结算结果不变、
            // 自动事件自然落到无变化的分支，因此不需要在事件里单独 early return。
            autoExpression.ExpressionChanged += _ => ApplyExpression();

            // ===== 设置变化统一接线 =====
            // SouffleSettings 是唯一事实源：任何写入方（托盘菜单/设置界面）只改属性，
            // 持久化与运行时副作用全部在这一个订阅里处理，写入方不做 Save、不直接调服务。
            settings.PropertyChanged += (_, e) =>
            {
                try { settings.Save(); } catch (Exception ex) { Logger.Warning(ex, "save settings failed"); }
                switch (e.PropertyName)
                {
                    case nameof(SouffleSettings.GazeFollowEnabled):
                        petOps.SetGazeFollow(settings.GazeFollowEnabled);
                        break;
                    case nameof(SouffleSettings.IsShowFps):
                        petOps.SetFpsVisible(settings.IsShowFps);
                        break;
                    case nameof(SouffleSettings.GamepadEnabled):
                        ApplyLinkage();
                        break;
                    case nameof(SouffleSettings.GamepadManual):
                    case nameof(SouffleSettings.MusicManual):
                        ApplyExpression();
                        break;
                    case nameof(SouffleSettings.StartWithWindows):
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

            // 结算当前生效的表情并同步到窗口。
            // 「游戏模式 / 音乐模式」这两个托盘开关在此优先级最高：开启期间无论自动检测
            // 命中什么都不改写表情；关闭后自动回落到 AutoExpressionService 当前命中的状态。
            void ApplyExpression()
            {
                var next = settings.GamepadManual ? settings.AutoExpressionGameExpression
                    : settings.MusicManual ? settings.AutoExpressionMusicExpression
                    : autoExpression.CurrentExpression;
                if (next == autoExpression.EffectiveExpression) return; // 无变化：不重复写参数、不重启服务
                autoExpression.EffectiveExpression = next;
                petOps.SetExpression(next);
                ApplyLinkage();
            }

            // 联动服务启停：同一时刻只有一个联动在跑（game > music），因此两者不会抢占。
            // 手柄侧：随生效表情一起也决定服务是否轮询——停下后不再每 33ms 查 XInput/键鼠/光标。
            // 口型侧：是否真的开始采集由 LipSyncService 内部按 LipSyncEnabled 把关，
            //         这里不重复判断（重复判断会让总开关重新勾选时服务的 music 态已被清掉、采不回来）。
            void ApplyLinkage()
            {
                var pad = settings.GamepadEnabled && autoExpression.EffectiveExpression == settings.AutoExpressionGameExpression;
                petOps.SetGamepadEnabled(pad);
                if (pad) gamepad.Start(); else gamepad.Stop();

                if (!pad && autoExpression.EffectiveExpression == settings.AutoExpressionMusicExpression) lipSync.Start();
                else lipSync.Stop();
            }

            petOps.SetGazeFollow(settings.GazeFollowEnabled);
            petOps.SetFpsVisible(settings.IsShowFps);
            // 应用持久化的初始状态（订阅只响应变化，初始值需显式应用一次）。
            // 此时模型尚未加载，表情由窗口侧缓存、模型就绪后补设（两类窗口都有该机制）。
            ApplyExpression();
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

            // 对话窗口热键（Ctrl+Alt+Space）：同样复用桌宠窗口句柄，须在 Show() 之后注册
            _provider.GetRequiredService<IChatWindowService>().Start(window);

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
