// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SoufflePet.Models;
using SoufflePet.Services;

namespace SoufflePet.ViewModels;

/// <summary>
/// 技能管理窗口的视图模型：左侧技能列表（可启用/停用）+ 右侧正文预览。
/// 技能每次打开窗口时从磁盘重新扫描；启用状态写回 <see cref="SouffleSettings.DisabledSkills"/>，
/// 持久化由 App 组合根统一处理。
/// </summary>
public partial class SkillsViewModel : ViewModelBase
{
    private static readonly ILogger Logger = Log.ForContext<SkillsViewModel>();

    private readonly SouffleSettings _settings;
    private readonly ISkillService _skills;

    public SkillsViewModel(SouffleSettings settings, ISkillService skills)
    {
        _settings = settings;
        _skills = skills;
        Reload();
    }

    /// <summary>技能列表（每次打开窗口时按磁盘内容重建）。</summary>
    public ObservableCollection<SkillItemViewModel> Skills { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial SkillItemViewModel? SelectedSkill { get; set; }

    public bool HasSelection => SelectedSkill is not null;

    /// <summary>宠物称呼（设置项）。</summary>
    public string PetName => _settings.PetName;

    public string WindowTitle => $"{PetName} · 技能";

    /// <summary>标题栏下方的说明文案。</summary>
    public string ToolHint => $"读取技能目录下的说明，按需交给{PetName}使用";

    /// <summary>扫描目录提示（相对应用目录）。</summary>
    public string SearchHint => string.Join(" 与 ", _skills.SearchDirectories.Select(d =>
        Path.GetRelativePath(AppContext.BaseDirectory, d).Replace('\\', '/')));

    /// <summary>列表上方的统计文案。</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    /// <summary>按磁盘内容重建列表（窗口每次显示前调用）。</summary>
    public void Reload()
    {
        var selectedName = SelectedSkill?.Name;

        foreach (var item in Skills) item.EnabledChanged -= OnItemEnabledChanged;
        Skills.Clear();

        foreach (var skill in _skills.Discover())
        {
            var item = new SkillItemViewModel(skill, _skills.ReadContent(skill), _skills.IsEnabled(skill));
            item.EnabledChanged += OnItemEnabledChanged;
            Skills.Add(item);
        }

        SelectedSkill = Skills.FirstOrDefault(s => s.Name == selectedName) ?? Skills.FirstOrDefault();
        UpdateStatus();
    }

    [RelayCommand]
    private void Refresh() => Reload();

    /// <summary>在资源管理器中打开技能目录；两个目录都不存在时打开应用目录。</summary>
    [RelayCommand]
    private void OpenFolder()
    {
        var target = _skills.SearchDirectories.FirstOrDefault(Directory.Exists) ?? AppContext.BaseDirectory;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "open skills folder {Path} failed", target);
        }
    }

    private void OnItemEnabledChanged(object? sender, EventArgs e)
    {
        _settings.DisabledSkills = [.. Skills.Where(s => !s.Enabled).Select(s => s.Name)];
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var enabled = Skills.Count(s => s.Enabled);
        Status = Skills.Count == 0 ? "没有找到技能" : $"共 {Skills.Count} 个技能 · 已启用 {enabled} 个";
    }
}
