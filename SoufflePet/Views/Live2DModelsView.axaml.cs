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
/// 模型管理面板本体（纯视图）：数据与命令来自 DataContext（Live2DModelsViewModel），
/// 代码后端只保留窗口级别的交互——拖动无边框窗口、关闭（隐藏）请求与选择模型压缩包。
/// </summary>
public partial class Live2DModelsView : UserControl
{
    private static readonly ILogger Logger = Log.ForContext<Live2DModelsView>();

    public Live2DModelsView() => InitializeComponent();

    /// <summary>标题栏按住拖动：无边框窗口没有系统标题栏，用 BeginMoveDrag 接管。</summary>
    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        (TopLevel.GetTopLevel(this) as Window)?.BeginMoveDrag(e);
    }

    /// <summary>关闭按钮只是隐藏窗口，真正关闭由 SettingsWindow.CloseForShutdown 负责。</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        (TopLevel.GetTopLevel(this) as Window)?.Hide();
    }

    /// <summary>导入模型：选一个模型压缩包，解压到模型目录并登记 exp3 / motion3。</summary>
    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not Live2DModelsViewModel viewModel) return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择模型压缩包",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("模型压缩包") { Patterns = ["*.zip"] }]
            });

            var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (string.IsNullOrEmpty(path)) return;

            viewModel.ImportFromArchive(path);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "pick live2d model archive failed");
        }
    }
}