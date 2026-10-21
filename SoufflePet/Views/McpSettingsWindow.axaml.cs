// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using Avalonia.Controls;
using Avalonia.Input;

namespace SoufflePet.Views;

/// <summary>
/// MCP 设置窗口外壳：只负责窗口属性（透明、无边框、置顶、固定尺寸）与窗口级行为
/// （Esc/关闭按钮只隐藏、退出时真正关闭）；面板内容由 ViewLocator 依 DataContext 解析。
/// </summary>
public partial class McpSettingsWindow : Window
{
    /// <summary>关闭按钮/Alt+F4 只隐藏窗口，应用退出时由服务调 CloseForShutdown 真正关闭。</summary>
    private bool _allowClose;

    public McpSettingsWindow()
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
