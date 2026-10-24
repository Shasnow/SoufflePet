// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SoufflePet.Models;
using SoufflePet.Services.Plugins;

namespace SoufflePet.ViewModels;

/// <summary>
/// 插件管理窗口的视图模型：左侧插件列表（可一键启用/禁用）+ 右侧详情。
/// 插件状态由 <see cref="IPluginService"/> 维护，窗口只读快照；开关变化立即调用服务加载/卸载。
/// </summary>
public partial class PluginsViewModel : ViewModelBase
{
    private static readonly ILogger Logger = Log.ForContext<PluginsViewModel>();

    private readonly SouffleSettings _settings;
    private readonly IPluginService _plugins;

    /// <summary>重建列表期间忽略开关事件，避免自己触发自己。</summary>
    private bool _reloading;

    public PluginsViewModel(SouffleSettings settings, IPluginService plugins)
    {
        _settings = settings;
        _plugins = plugins;
        _plugins.Changed += OnPluginsChanged;
        Reload();
    }

    /// <summary>插件列表（每次打开窗口时按服务快照重建）。</summary>
    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial PluginItemViewModel? SelectedPlugin { get; set; }

    public bool HasSelection => SelectedPlugin is not null;

    /// <summary>宠物称呼（设置项）。</summary>
    public string PetName => _settings.PetName;

    public string WindowTitle => $"{PetName} · 插件";

    /// <summary>标题栏下方的说明文案。</summary>
    public string ToolHint => "插件可向对话模型提供工具，或对模型回复做后处理；启停与配置保存立即生效";

    /// <summary>列表上方的统计文案。</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    /// <summary>导入/切换结果提示（成功与失败都用这一行，空串表示不显示）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string Message { get; set; } = string.Empty;

    public bool HasMessage => Message.Length > 0;

    /// <summary>按服务当前快照重建列表（窗口每次显示前调用）。</summary>
    public void Reload()
    {
        if (_reloading) return;

        _reloading = true;
        try
        {
            var selectedId = SelectedPlugin?.Id;

            foreach (var item in Plugins)
            {
                item.EnabledChanged -= OnItemEnabledChanged;
                item.ConfigSaveRequested -= OnItemConfigSaveRequested;
            }
            Plugins.Clear();

            foreach (var descriptor in _plugins.Plugins)
            {
                var item = new PluginItemViewModel(descriptor);
                item.EnabledChanged += OnItemEnabledChanged;
                item.ConfigSaveRequested += OnItemConfigSaveRequested;
                Plugins.Add(item);
            }

            SelectedPlugin = Plugins.FirstOrDefault(p => p.Id == selectedId) ?? Plugins.FirstOrDefault();
            UpdateStatus();
        }
        finally
        {
            _reloading = false;
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        _plugins.Discover();
        Reload();
    }

    /// <summary>从压缩包导入插件：安装后重新扫描，并选中刚装好的插件。</summary>
    public void InstallFromArchive(string archivePath)
    {
        var result = _plugins.Install(archivePath);
        Message = result.Message;
        if (!result.Success) return;

        Reload();
        if (result.PluginId is { Length: > 0 } id)
        {
            SelectedPlugin = Plugins.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                             ?? SelectedPlugin;
        }
    }

    /// <summary>在资源管理器中打开插件目录；都不存在时打开（并创建）安装目录。</summary>
    [RelayCommand]
    private void OpenFolder()
    {
        var target = _plugins.SearchDirectories.FirstOrDefault(Directory.Exists) ?? _plugins.InstallDirectory;
        try
        {
            Directory.CreateDirectory(target);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "open plugins folder {Path} failed", target);
        }
    }

    private void OnPluginsChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(Reload);

    private async void OnItemEnabledChanged(object? sender, EventArgs e)
    {
        if (_reloading || sender is not PluginItemViewModel item) return;

        try
        {
            await _plugins.SetEnabledAsync(item.Id, item.Enabled);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "toggle plugin {Plugin} failed", item.Id);
            Message = $"切换插件状态失败：{ex.Message}";
        }

        Reload();
    }

    private async void OnItemConfigSaveRequested(object? sender, EventArgs e)
    {
        if (_reloading || sender is not PluginItemViewModel item) return;

        try
        {
            await _plugins.SaveConfigAsync(item.Id, item.BuildConfig());
            Message = $"已保存「{item.Name}」的配置";
            Reload();
        }
        catch (FormatException ex)
        {
            Message = ex.Message;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "save plugin {Plugin} config failed", item.Id);
            Message = $"保存配置失败：{ex.Message}";
        }
    }

    private void UpdateStatus()
    {
        var enabled = Plugins.Count(p => p.Enabled);
        Status = Plugins.Count == 0 ? "没有找到插件" : $"共 {Plugins.Count} 个插件 · 已启用 {enabled} 个";
    }
}
