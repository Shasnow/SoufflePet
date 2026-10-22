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
/// 技能管理面板本体（纯视图）：数据与命令来自 DataContext（SkillsViewModel），
/// 代码后端只保留窗口级别的交互——拖动无边框窗口、关闭（隐藏）请求与选择安装包。
/// </summary>
public partial class SkillsView : UserControl
{
    private static readonly ILogger Logger = Log.ForContext<SkillsView>();

    public SkillsView() => InitializeComponent();

    /// <summary>标题栏按住拖动：无边框窗口没有系统标题栏，用 BeginMoveDrag 接管。</summary>
    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        (TopLevel.GetTopLevel(this) as Window)?.BeginMoveDrag(e);
    }

    /// <summary>关闭按钮只是隐藏窗口，真正关闭由 SkillsWindow.CloseForShutdown 负责。</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        (TopLevel.GetTopLevel(this) as Window)?.Hide();
    }

    /// <summary>安装技能：选一个含 SKILL.md 的压缩包，交给视图模型解压到技能目录。</summary>
    private async void OnInstallClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SkillsViewModel viewModel) return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择技能压缩包",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("技能压缩包") { Patterns = ["*.zip"] }]
            });

            var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (string.IsNullOrEmpty(path)) return;

            viewModel.InstallFromArchive(path);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "pick skill archive failed");
        }
    }
}
