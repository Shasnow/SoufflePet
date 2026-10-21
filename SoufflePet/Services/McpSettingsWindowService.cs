// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using Avalonia.Controls;
using SoufflePet.ViewModels;
using SoufflePet.Views;

namespace SoufflePet.Services;

/// <summary>MCP 服务器设置窗口的呼出服务（DI 注册）：托盘菜单调用。</summary>
public interface IMcpSettingsWindowService
{
    /// <summary>显示设置窗口（每次显示前重载配置，首次调用时创建窗口）。</summary>
    void Show();
}

/// <summary>MCP 设置窗口服务：单例视图模型 + 懒建窗口，关闭只隐藏。</summary>
public sealed class McpSettingsWindowService(McpSettingsViewModel viewModel) : IMcpSettingsWindowService, IDisposable
{
    private McpSettingsWindow? _window;

    public void Show()
    {
        var window = _window ??= new McpSettingsWindow { DataContext = viewModel };
        viewModel.Reload(); // 窗口隐藏期间设置文件可能被手改，显示前以文件为准

        if (window.IsVisible)
        {
            window.Activate();
            return;
        }

        window.Show();
        window.Activate();
    }

    public void Dispose()
    {
        _window?.CloseForShutdown();
        _window = null;
    }
}
