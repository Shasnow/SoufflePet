// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using SoufflePet.ViewModels;
using SoufflePet.Views;

namespace SoufflePet.Services;

/// <summary>技能管理窗口的显示入口。</summary>
public interface ISkillsWindowService
{
    void Show();
}

/// <summary>懒创建技能窗口并复用同一实例；每次显示前重新扫描技能目录。</summary>
public sealed class SkillsWindowService(SkillsViewModel viewModel) : ISkillsWindowService, IDisposable
{
    private SkillsWindow? _window;

    public void Show()
    {
        var window = _window ??= new SkillsWindow { DataContext = viewModel };
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
