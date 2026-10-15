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
public sealed class TrayService(
    IPetWindow petOps,
    IAutoExpressionService autoExpression,
    ILipSyncService lipSync,
    AutoExpressionConfig config)
    : ITrayService, IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<TrayService>();

    private TrayIcons? _trayIcons;
    private Application? _app;

    public void Show(Application app, IClassicDesktopStyleApplicationLifetime desktop)
    {
        _app = app;
        var menu = new NativeMenu();

        var toggleItem = new NativeMenuItem("显示 / 隐藏");
        toggleItem.Click += (_, _) => petOps.ToggleVisible();

        var resetItem = new NativeMenuItem("重置模型");
        resetItem.Click += (_, _) => petOps.ResetModel();

        // 自动表情联动：检测到游戏/音乐应用自动切换表情（进程白名单见 autoexpression.json）
        var autoExprItem = new NativeMenuItem("自动表情")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = config.Enabled,
        };
        autoExprItem.Click += (_, _) =>
        {
            // 勾选状态变化也走 Click 事件，此时 IsChecked 已是新值
            autoExpression.Enabled = autoExprItem.IsChecked;
            Logger.Information("auto expression toggled enabled={Enabled}", autoExprItem.IsChecked);
        };

        // 音乐口型律动：自动表情处于 music 状态时，系统播放声音驱动嘴部开合
        var lipSyncItem = new NativeMenuItem("口型律动")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = lipSync.Enabled,
        };
        lipSyncItem.Click += (_, _) =>
        {
            // 勾选状态变化也走 Click 事件，此时 IsChecked 已是新值
            lipSync.Enabled = lipSyncItem.IsChecked;
        };

        // 目光跟随：视线持续朝向全局光标（关闭时视线回正）
        var gazeItem = new NativeMenuItem("目光跟随")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = config.GazeFollowEnabled,
        };
        gazeItem.Click += (_, _) =>
        {
            // 勾选状态变化也走 Click 事件，此时 IsChecked 已是新值
            var enabled = gazeItem.IsChecked;
            config.GazeFollowEnabled = enabled;
            try { config.Save(); } catch (Exception ex) { Logger.Warning(ex, "save config failed"); }
            petOps.SetGazeFollow(enabled);
            Logger.Information("gaze follow toggled enabled={Enabled}", enabled);
        };

        // 帧率显示：窗口右上角 当前/上限 叠加层
        var fpsItem = new NativeMenuItem("显示帧率")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = config.FpsOverlayEnabled,
        };
        fpsItem.Click += (_, _) =>
        {
            // 勾选状态变化也走 Click 事件，此时 IsChecked 已是新值
            var visible = fpsItem.IsChecked;
            config.FpsOverlayEnabled = visible;
            try { config.Save(); } catch (Exception ex) { Logger.Warning(ex, "save config failed"); }
            petOps.SetFpsVisible(visible);
            Logger.Information("fps overlay toggled visible={Visible}", visible);
        };

        // 手柄联动：命中游戏时把手柄（或 WASD + 鼠标）输入写进模型摇杆/按键参数
        var gamepadItem = new NativeMenuItem("手柄联动")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = config.GamepadEnabled,
        };
        gamepadItem.Click += (_, _) =>
        {
            var enabled = gamepadItem.IsChecked;
            config.GamepadEnabled = enabled;
            try { config.Save(); } catch (Exception ex) { Logger.Warning(ex, "save config failed"); }
            RefreshGamepadDriving();
            Logger.Information("gamepad linkage toggled enabled={Enabled}", enabled);
        };

        // 常显手柄：不等待游戏检测，直接显示手柄并驱动（没打游戏时调试/演示用）
        var manualPadItem = new NativeMenuItem("常显手柄")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = config.GamepadManual,
        };
        manualPadItem.Click += (_, _) =>
        {
            var on = manualPadItem.IsChecked;
            config.GamepadManual = on;
            try { config.Save(); } catch (Exception ex) { Logger.Warning(ex, "save config failed"); }
            if (on)
            {
                petOps.SetAutoExpression(config.GameExpression);
            }
            else
            {
                // 交回自动检测：先清掉手动设置的表情，再立刻轮询一次（命中游戏会马上切回来）
                petOps.SetAutoExpression(null);
                petOps.SetGamepadEnabled(false);
                autoExpression.PollNow();
            }
            RefreshGamepadDriving();
            Logger.Information("manual gamepad mode toggled on={On}", on);
        };

        var exitItem = new NativeMenuItem("退出");
        exitItem.Click += (_, _) =>
        {
            petOps.ExitForShutdown();
            desktop.Shutdown();
        };

        menu.Items.Add(toggleItem);
        menu.Items.Add(resetItem);
        menu.Items.Add(autoExprItem);
        menu.Items.Add(lipSyncItem);
        menu.Items.Add(gazeItem);
        menu.Items.Add(gamepadItem);
        menu.Items.Add(manualPadItem);
        menu.Items.Add(fpsItem);
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

    /// <summary>
    /// 重新评估手柄驱动开关：总开关开 且（手动模式 或 当前正处于"手柄"表情）时才驱动。
    /// 托盘切换开关后立即调用，不必等下一个自动检测周期。
    /// </summary>
    private void RefreshGamepadDriving()
    {
        var on = config.GamepadEnabled
              && (config.GamepadManual || autoExpression.CurrentExpression == config.GameExpression);
        petOps.SetGamepadEnabled(on);
    }

    public void Dispose()
    {
        // 移除托盘图标（进程退出前清理）
        if (_app != null && _trayIcons != null) TrayIcon.SetIcons(_app, []);
        _trayIcons = null;
    }
}
