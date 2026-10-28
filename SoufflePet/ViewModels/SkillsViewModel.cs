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
    [NotifyPropertyChangedFor(nameof(UninstallPrompt))]
    public partial SkillItemViewModel? SelectedSkill { get; set; }

    public bool HasSelection => SelectedSkill is not null;

    /// <summary>宠物称呼（设置项）。</summary>
    public string PetName => _settings.PetName;

    public string WindowTitle => $"{PetName} · 技能";

    /// <summary>标题栏下方的说明文案。</summary>
    public string ToolHint => $"读取技能目录下的说明，按需交给{PetName}使用";

    /// <summary>扫描目录提示：应用目录内用相对路径，家目录用 ~ 开头。</summary>
    public string SearchHint => string.Join(" 与 ", _skills.SearchDirectories.Select(_skills.DescribeDirectory));

    /// <summary>列表上方的统计文案。</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    /// <summary>安装/卸载结果提示（成功与失败都用这一行，空串表示不显示）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultMessage))]
    public partial string ResultMessage { get; set; } = string.Empty;

    public bool HasResultMessage => ResultMessage.Length > 0;

    /// <summary>卸载二次确认是否显示：技能目录会整个从磁盘删除，先确认再动手。</summary>
    [ObservableProperty]
    public partial bool IsConfirmingUninstall { get; set; }

    public string UninstallPrompt => SelectedSkill is { } skill
        ? $"「{skill.Name}」将从磁盘删除：\n{skill.FilePath}"
        : string.Empty;

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

        // 工作区可能已经切走，提示行里的扫描目录要跟着刷新
        OnPropertyChanged(nameof(SearchHint));
        UpdateStatus();
    }

    [RelayCommand]
    private void Refresh() => Reload();

    /// <summary>从压缩包安装技能：安装后重新扫描，并选中刚装好的技能。</summary>
    public void InstallFromArchive(string archivePath)
    {
        var result = _skills.Install(archivePath);
        ResultMessage = result.Message;
        if (!result.Success) return;

        Reload();
        if (result.SkillName is { Length: > 0 } name)
        {
            SelectedSkill = Skills.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                            ?? SelectedSkill;
        }
    }

    /// <summary>点击卸载：先弹出二次确认，确认后才真正删盘。</summary>
    [RelayCommand]
    private void RequestUninstall()
    {
        if (SelectedSkill is null) return;
        ResultMessage = string.Empty;
        OnPropertyChanged(nameof(UninstallPrompt));
        IsConfirmingUninstall = true;
    }

    [RelayCommand]
    private void CancelUninstall() => IsConfirmingUninstall = false;

    /// <summary>确认卸载：删除技能目录（单个 *.md 技能则删文件），成功后重新扫描列表。</summary>
    [RelayCommand]
    private void ConfirmUninstall()
    {
        IsConfirmingUninstall = false;
        if (SelectedSkill is not { } skill) return;

        var result = _skills.Uninstall(skill.Skill);
        ResultMessage = result.Message;
        if (!result.Success) return;

        Reload();
        PersistDisabledSkills();
    }

    /// <summary>在资源管理器中打开技能目录；都不存在时打开（并创建）安装目录。</summary>
    [RelayCommand]
    private void OpenFolder()
    {
        var target = _skills.SearchDirectories.FirstOrDefault(Directory.Exists) ?? _skills.InstallDirectory;
        try
        {
            Directory.CreateDirectory(target);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "open skills folder {Path} failed", target);
        }
    }

    private void OnItemEnabledChanged(object? sender, EventArgs e)
    {
        PersistDisabledSkills();
        UpdateStatus();
    }

    /// <summary>把当前列表的启用状态写回设置（卸载后也要重新写，去掉已删除技能的名字）。</summary>
    private void PersistDisabledSkills() =>
        _settings.DisabledSkills = [.. Skills.Where(s => !s.Enabled).Select(s => s.Name)];

    private void UpdateStatus()
    {
        var enabled = Skills.Count(s => s.Enabled);
        Status = Skills.Count == 0 ? "没有找到技能" : $"共 {Skills.Count} 个技能 · 已启用 {enabled} 个";
    }
}
