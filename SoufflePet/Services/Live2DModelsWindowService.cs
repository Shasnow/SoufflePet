// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using SoufflePet.ViewModels;
using SoufflePet.Views;

namespace SoufflePet.Services;

/// <summary>模型管理窗口的显示入口。</summary>
public interface ILive2DModelsWindowService
{
    void Show();
}

/// <summary>懒创建模型管理面板并复用同一实例（共用外壳 SettingsWindow）。</summary>
public sealed class Live2DModelsWindowService(Live2DModelsViewModel viewModel) : ILive2DModelsWindowService, IDisposable
{
    private SettingsWindow? _window;

    public void Show()
    {
        var window = _window ??= new SettingsWindow { DataContext = viewModel };
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