// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using SoufflePet.ViewModels;
using SoufflePet.Views;

namespace SoufflePet.Services;

/// <summary>插件管理窗口的显示入口。</summary>
public interface IPluginsWindowService
{
    void Show();
}

/// <summary>懒创建插件面板并复用同一实例（共用外壳 SettingsWindow）；每次显示前重新扫描插件目录。</summary>
public sealed class PluginsWindowService(PluginsViewModel viewModel) : IPluginsWindowService, IDisposable
{
    private SettingsWindow? _window;

    public void Show()
    {
        var window = _window ??= new SettingsWindow { DataContext = viewModel };
        viewModel.Reload();
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
