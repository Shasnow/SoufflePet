// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using SoufflePet.ViewModels;
using SoufflePet.Views;

namespace SoufflePet.Services;

/// <summary>定时任务管理窗口的显示入口。</summary>
public interface IScheduledTasksWindowService
{
    void Show();
}

/// <summary>懒创建定时任务面板并复用同一实例（共用外壳 SettingsWindow）；每次显示前重排订阅与触发计划。</summary>
public sealed class ScheduledTasksWindowService(ScheduledTasksViewModel viewModel)
    : IScheduledTasksWindowService, IDisposable
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