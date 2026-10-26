// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Serilog;
using SoufflePet.Models;
using SoufflePet.Views;

namespace SoufflePet.Services;

/// <summary>托盘图标与菜单服务抽象（DI 注册）。</summary>
public interface ITrayService
{
    /// <summary>创建并显示托盘图标与菜单。</summary>
    void Show(Application app, IClassicDesktopStyleApplicationLifetime desktop);
}

/// <summary>
/// 托盘服务：构建托盘图标与菜单。开关型菜单项只负责把用户意图写进 SouffleSettings 属性，
/// 持久化与运行时副作用（窗口/服务的应用）统一由 App 组合根的 settings.PropertyChanged
/// 订阅处理，此处不做任何 Save 或服务调用；瞬时动作项（显示 / 隐藏、重置模型、对话窗口、退出）
/// 则直接调用对应服务。
/// </summary>
public sealed class TrayService(
    IPetWindow petOps,
    IChatWindowService chat,
    SouffleSettings settings)
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

        // 对话窗口：与 Ctrl+Alt+Space / 左键单击桌宠相同的切换逻辑，打开时停靠在桌宠窗口旁边
        var chatItem = new NativeMenuItem("对话窗口");
        chatItem.Click += (_, _) => OpenChatWindow(desktop);

        // 自动表情联动：检测到游戏/音乐应用自动切换表情（进程白名单见 SoufflePet.json）
        var autoExprItem = NewToggle("自动表情", settings.AutoExpressionEnabled,
            v => settings.AutoExpressionEnabled = v);

        // 音乐口型律动：自动表情处于 music 状态时，系统播放声音驱动嘴部开合
        var lipSyncItem = NewToggle("口型律动", settings.LipSyncEnabled,
            v => settings.LipSyncEnabled = v);

        // 目光跟随：视线持续朝向全局光标（关闭时视线回正）
        var gazeItem = NewToggle("目光跟随", settings.GazeFollowEnabled,
            v => settings.GazeFollowEnabled = v);

        // 输入联动：命中游戏时把手柄（或 WASD + 鼠标）输入写进模型摇杆/按键参数
        var gamepadItem = NewToggle("输入联动", settings.GamepadEnabled,
            v => settings.GamepadEnabled = v);

        // 游戏模式：不等待游戏检测，直接进入游戏模式并驱动（没打游戏时调试/演示用）
        var manualPadItem = NewToggle("游戏模式", settings.GamepadManual,
            v => settings.GamepadManual = v);

        // 音乐模式：不等待音乐检测，直接进入音乐模式并驱动口型律动（没放音乐时调试/演示用）
        var manualMusicItem = NewToggle("音乐模式", settings.MusicManual,
            v => settings.MusicManual = v);

        // 帧率显示：窗口右上角帧率叠加层
        var fpsItem = NewToggle("显示帧率", settings.IsShowFps,
            v => settings.IsShowFps = v);

        // 开机自启：写入/移除 HKCU Run 注册表项（副作用统一由 App 订阅处理）
        var autoStartItem = NewToggle("开机自启", settings.StartWithWindows,
            v => settings.StartWithWindows = v);

        var exitItem = new NativeMenuItem("退出");
        exitItem.Click += (_, _) =>
        {
            petOps.ExitForShutdown();
            desktop.Shutdown();
        };

        menu.Items.Add(toggleItem);
        menu.Items.Add(resetItem);
        menu.Items.Add(chatItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(autoExprItem);
        menu.Items.Add(lipSyncItem);
        menu.Items.Add(gazeItem);
        menu.Items.Add(gamepadItem);
        menu.Items.Add(manualPadItem);
        menu.Items.Add(manualMusicItem);
        menu.Items.Add(fpsItem);
        menu.Items.Add(autoStartItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        var icon = new Bitmap(AssetLoader.Open(new Uri("avares://SoufflePet/Assets/pet.ico")));
        var trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(icon),
            ToolTipText = "SoufflePet",
            Menu = menu,
        };
        _trayIcons = [trayIcon];
        TrayIcon.SetIcons(app, _trayIcons);
        Logger.Information("Tray icon set, initialization completed");
    }

    /// <summary>
    /// 呼出对话窗口：以桌宠窗口为停靠锚点（即 Ctrl+Alt+Space 的锚点）。
    /// 桌宠窗口可能处于隐藏状态，此时位置仍可读，锚点照常可用。
    /// </summary>
    private void OpenChatWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (desktop.MainWindow is not Window anchor)
        {
            Logger.Warning("tray chat item ignored: pet window not available");
            return;
        }

        chat.Toggle(anchor);
    }

    /// <summary>构造勾选菜单项：初值取自 settings，点击只把新值写回 settings（副作用统一走订阅）。</summary>
    private static NativeMenuItem NewToggle(string header, bool initial, Action<bool> apply)
    {
        var item = new NativeMenuItem(header)
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = initial,
        };
        item.Click += (_, _) => apply(item.IsChecked); // 勾选状态变化也走 Click 事件，此时 IsChecked 已是新值
        return item;
    }

    public void Dispose()
    {
        // 移除托盘图标（进程退出前清理）
        if (_app != null && _trayIcons != null) TrayIcon.SetIcons(_app, []);
        _trayIcons = null;
    }
}
