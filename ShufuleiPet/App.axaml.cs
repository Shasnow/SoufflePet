using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Serilog;
using ShufuleiPet.Services;
using ShufuleiPet.Views;

namespace ShufuleiPet;

public partial class App : Application
{
    private static readonly ILogger Logger = Log.ForContext<App>();

    private TrayIcons? _trayIcons;
    private AutoExpressionService? _autoExpression;

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
            Window window;
            IPetWindow petOps;
            if (useNative)
            {
                var native = new NativePetWindow();
                window = native;
                petOps = native;
            }
            else
            {
                var web = new MainWindow();
                window = web;
                petOps = web;
            }
            desktop.MainWindow = window;
            Logger.Debug("Main window assigned");
            try
            {
                window.Show();
                Logger.Information("Window shown visible={Visible} hwnd={Hwnd}", window.IsVisible, window.TryGetPlatformHandle()?.Handle);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "window.Show() failed");
            }

            var menu = new NativeMenu();
            var toggleItem = new NativeMenuItem("显示 / 隐藏");
            toggleItem.Click += (_, _) => petOps.ToggleVisible();
            var resetItem = new NativeMenuItem("重置模型");
            resetItem.Click += (_, _) => petOps.ResetModel();

            // 自动表情联动：检测到游戏/音乐进程自动切换表情（进程白名单见 autoexpression.json）
            var cfg = AutoExpressionConfig.Load();
            _autoExpression = new AutoExpressionService(cfg, petOps.SetAutoExpression);
            var autoExprItem = new NativeMenuItem("自动表情")
            {
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = cfg.Enabled,
            };
            autoExprItem.Click += (_, _) =>
            {
                // 勾选状态变化也走 Click 事件，此时 IsChecked 已是新值
                _autoExpression.Enabled = autoExprItem.IsChecked;
                Logger.Information("auto expression toggled enabled={Enabled}", autoExprItem.IsChecked);
            };

            var exitItem = new NativeMenuItem("退出");
            exitItem.Click += (_, _) =>
            {
                _autoExpression?.Dispose();
                petOps.ExitForShutdown();
                desktop.Shutdown();
            };
            menu.Items.Add(toggleItem);
            menu.Items.Add(resetItem);
            menu.Items.Add(autoExprItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(exitItem);

            var icon = new Bitmap(AssetLoader.Open(new Uri("avares://ShufuleiPet/Assets/pet.ico")));
            var trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(icon),
                ToolTipText = "舒芙蕾桌宠",
                Menu = menu,
            };
            _trayIcons = [trayIcon];
            TrayIcon.SetIcons(this, _trayIcons);
            Logger.Information("Tray icon set, initialization completed");
        }

        base.OnFrameworkInitializationCompleted();
    }
}
