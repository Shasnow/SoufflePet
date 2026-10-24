// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Serilog;
using SoufflePet.ViewModels;

namespace SoufflePet.Views;

/// <summary>
/// 插件管理窗口：透明无边框外框 + 面板内容合为一体。
/// 关闭按钮/Esc/Alt+F4 只隐藏窗口，应用退出时由服务调 CloseForShutdown 真正关闭。
/// </summary>
public partial class PluginsWindow : Window
{
    private static readonly ILogger Logger = Log.ForContext<PluginsWindow>();

    /// <summary>关闭按钮/Alt+F4 只隐藏窗口，应用退出时由服务调 CloseForShutdown 真正关闭。</summary>
    private bool _allowClose;

    public PluginsWindow()
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

    /// <summary>标题栏按住拖动：无边框窗口没有系统标题栏，用 BeginMoveDrag 接管。</summary>
    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        BeginMoveDrag(e);
    }

    /// <summary>关闭按钮只是隐藏窗口，真正关闭由 CloseForShutdown 负责。</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Hide();

    /// <summary>导入插件：选一个含 plugin.json 的压缩包，交给视图模型解压到插件目录。</summary>
    private async void OnInstallClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PluginsViewModel viewModel) return;

        var storage = StorageProvider;
        if (storage is null) return;

        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择插件压缩包",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("插件压缩包") { Patterns = ["*.zip"] }]
            });

            var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (string.IsNullOrEmpty(path)) return;

            viewModel.InstallFromArchive(path);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "pick plugin archive failed");
        }
    }
}
