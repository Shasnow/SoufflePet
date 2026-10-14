using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Serilog;
using ShufuleiPet.Views;

namespace ShufuleiPet.Services;

/// <summary>托盘图标与菜单服务抽象（DI 注册）。</summary>
public interface ITrayService
{
    /// <summary>创建并显示托盘图标与菜单。</summary>
    void Show(Application app, IClassicDesktopStyleApplicationLifetime desktop);
}

/// <summary>
/// 托盘服务：构建托盘图标与菜单（显示/隐藏、重置模型、自动表情开关、退出），
/// 菜单项逻辑依赖 IPetWindow 与 IAutoExpressionService（构造注入）。
/// </summary>
public sealed class TrayService : ITrayService, IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<TrayService>();

    private readonly IPetWindow _petOps;
    private readonly IAutoExpressionService _autoExpression;
    private readonly ILipSyncService _lipSync;
    private readonly AutoExpressionConfig _config;
    private TrayIcons? _trayIcons;
    private Application? _app;

    public TrayService(IPetWindow petOps, IAutoExpressionService autoExpression, ILipSyncService lipSync, AutoExpressionConfig config)
    {
        _petOps = petOps;
        _autoExpression = autoExpression;
        _lipSync = lipSync;
        _config = config;
    }

    public void Show(Application app, IClassicDesktopStyleApplicationLifetime desktop)
    {
        _app = app;
        var menu = new NativeMenu();

        var toggleItem = new NativeMenuItem("显示 / 隐藏");
        toggleItem.Click += (_, _) => _petOps.ToggleVisible();

        var resetItem = new NativeMenuItem("重置模型");
        resetItem.Click += (_, _) => _petOps.ResetModel();

        // 自动表情联动：检测到游戏/音乐应用自动切换表情（进程白名单见 autoexpression.json）
        var autoExprItem = new NativeMenuItem("自动表情")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _config.Enabled,
        };
        autoExprItem.Click += (_, _) =>
        {
            // 勾选状态变化也走 Click 事件，此时 IsChecked 已是新值
            _autoExpression.Enabled = autoExprItem.IsChecked;
            Logger.Information("auto expression toggled enabled={Enabled}", autoExprItem.IsChecked);
        };

        // 音乐口型律动：自动表情处于 music 状态时，系统播放声音驱动嘴部开合
        var lipSyncItem = new NativeMenuItem("口型律动")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _lipSync.Enabled,
        };
        lipSyncItem.Click += (_, _) =>
        {
            // 勾选状态变化也走 Click 事件，此时 IsChecked 已是新值
            _lipSync.Enabled = lipSyncItem.IsChecked;
        };

        var exitItem = new NativeMenuItem("退出");
        exitItem.Click += (_, _) =>
        {
            _petOps.ExitForShutdown();
            desktop.Shutdown();
        };

        menu.Items.Add(toggleItem);
        menu.Items.Add(resetItem);
        menu.Items.Add(autoExprItem);
        menu.Items.Add(lipSyncItem);
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
        TrayIcon.SetIcons(app, _trayIcons);
        Logger.Information("Tray icon set, initialization completed");
    }

    public void Dispose()
    {
        // 移除托盘图标（进程退出前清理）
        if (_app != null && _trayIcons != null) TrayIcon.SetIcons(_app, []);
        _trayIcons = null;
    }
}
