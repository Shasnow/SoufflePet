// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using Avalonia.Controls;
using Avalonia.Input;

namespace SoufflePet.Views;

/// <summary>
/// 各设置面板（MCP 服务器 / 技能 / 插件 / 定时任务）共用的窗口外壳：
/// 只负责窗口级属性与行为——透明无边框、不可缩放、居中的置顶小窗，关闭/Esc 只隐藏、应用退出时才真正关闭；
/// 面板内容交给 ViewLocator，依 DataContext 自动实例化对应的 XxxView。
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>关闭按钮/Alt+F4 只隐藏窗口，应用退出时由服务调 CloseForShutdown 真正关闭。</summary>
    private bool _allowClose;

    public SettingsWindow()
    {
        InitializeComponent();

        Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            Hide();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Hide();
        };
    }

    /// <summary>应用退出时真正关闭窗口（平时关闭只隐藏，便于再次打开）。</summary>
    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }
}