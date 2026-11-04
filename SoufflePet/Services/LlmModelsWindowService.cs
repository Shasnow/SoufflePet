// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using Avalonia;
using SoufflePet.ViewModels;
using SoufflePet.Views;

namespace SoufflePet.Services;

/// <summary>对话模型管理窗口的显示入口。</summary>
public interface ILlmModelsWindowService
{
    void Show();
}

/// <summary>懒创建对话模型管理面板并复用同一实例（共用外壳 SettingsWindow）。</summary>
public sealed class LlmModelsWindowService(LlmModelsViewModel viewModel) : ILlmModelsWindowService, IDisposable
{
    private SettingsWindow? _window;

    public void Show()
    {
        var window = _window ??= CreateWindow();
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

    private SettingsWindow CreateWindow()
    {
        var window = new SettingsWindow { DataContext = viewModel };
        // 关闭按钮 / Esc 只是隐藏窗口（真正关闭在 Dispose 里）：隐藏时取消在途的模型列表请求，
        // 否则下次打开时按钮会灰在「获取中」，要等那个请求自己超时（最长 30 秒）才恢复。
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty && !window.IsVisible) viewModel.CancelFetch();
        };

        return window;
    }
}
