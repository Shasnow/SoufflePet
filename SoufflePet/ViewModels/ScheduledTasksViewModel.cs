// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SoufflePet.Models;
using SoufflePet.Services;

namespace SoufflePet.ViewModels;

/// <summary>
/// 定时任务管理窗口的视图模型：左侧任务列表（启用/停用、下次触发时间）+ 右侧编辑区（调度方式与任务内容）+ 底部执行日志。
/// 任务配置随 <see cref="SouffleSettings.ScheduledTasks" /> 持久化，调度由 <see cref="IScheduledTaskService" /> 负责；
/// 任一处修改都即时重排触发计划，但配置不自动落盘，需点「保存」按钮才写入 SoufflePet.json。
/// </summary>
public partial class ScheduledTasksViewModel : ViewModelBase
{
    private static readonly ILogger Logger = Log.ForContext<ScheduledTasksViewModel>();

    private readonly SouffleSettings _settings;
    private readonly IScheduledTaskService _scheduler;

    /// <summary>已订阅 PropertyChanged 的任务。</summary>
    private readonly List<ScheduledTask> _tracked = [];

    /// <summary>当前已挂上星期勾选订阅的任务。</summary>
    private ScheduledTask? _weekdayOwner;

    public ScheduledTasksViewModel(SouffleSettings settings, IScheduledTaskService scheduler)
    {
        _settings = settings;
        _scheduler = scheduler;

        // 日志集合增删时刷新空状态提示的显隐
        Logs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLogs));

        Reload();
    }

    /// <summary>任务列表（与设置共享同一集合）。</summary>
    public ObservableCollection<ScheduledTask> Tasks => _settings.ScheduledTasks;

    /// <summary>执行日志（最新在前，与调度服务共享）。</summary>
    public ObservableCollection<ScheduledTaskLogEntry> Logs => _scheduler.Logs;

    public string PetName => _settings.PetName;

    public string WindowTitle => $"{PetName} · 定时任务";

    public string ToolHint => $"到点后由{PetName}按任务内容发起一轮对话";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial ScheduledTask? SelectedTask { get; set; }

    public bool HasSelection => SelectedTask is not null;

    public bool HasTasks => Tasks.Count > 0;

    public bool HasLogs => Logs.Count > 0;

    /// <summary>列表上方的统计文案。</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    /// <summary>按当前集合重建订阅与选中项（窗口每次显示前调用），并刷新各任务的下次触发时间。</summary>
    public void Reload()
    {
        foreach (var task in _tracked) task.PropertyChanged -= OnTaskPropertyChanged;
        _tracked.Clear();

        foreach (var task in Tasks)
        {
            task.PropertyChanged += OnTaskPropertyChanged;
            _tracked.Add(task);
        }

        _scheduler.Refresh();

        if (SelectedTask is null || !Tasks.Contains(SelectedTask))
            SelectedTask = Tasks.FirstOrDefault();

        OnPropertyChanged(nameof(HasTasks));
        UpdateStatus();
    }

    [RelayCommand]
    private void AddTask()
    {
        var task = new ScheduledTask { Name = MakeUniqueName(), Kind = ScheduledTaskKind.Daily };
        task.PropertyChanged += OnTaskPropertyChanged;
        _tracked.Add(task);
        Tasks.Add(task);

        OnPropertyChanged(nameof(HasTasks));
        _scheduler.Refresh();
        SelectedTask = task;
        UpdateStatus();
    }

    [RelayCommand]
    private void DeleteTask()
    {
        if (SelectedTask is not { } task) return;

        task.PropertyChanged -= OnTaskPropertyChanged;
        _tracked.Remove(task);
        Tasks.Remove(task);

        OnPropertyChanged(nameof(HasTasks));
        _scheduler.Refresh();
        SelectedTask = Tasks.FirstOrDefault();
        UpdateStatus();
    }

    /// <summary>立即执行选中的任务；不影响其原有触发计划。</summary>
    [RelayCommand]
    private void RunNow()
    {
        if (SelectedTask is not { } task) return;
        _scheduler.RunNow(task);
    }

    [RelayCommand]
    private void ClearLogs() => _scheduler.ClearLogs();

    /// <summary>把当前任务配置写入 SoufflePet.json（手动保存，不再随输入自动落盘）。</summary>
    [RelayCommand]
    private void Save() => SaveSettings();

    /// <summary>选中任务变化：把星期勾选订阅从一个任务迁到另一个，并同步勾选状态。</summary>
    partial void OnSelectedTaskChanged(ScheduledTask? value)
    {
        if (_weekdayOwner is not null)
        {
            foreach (var option in _weekdayOwner.WeekdayOptions) option.PropertyChanged -= OnWeekdayChanged;
        }

        _weekdayOwner = value;

        if (value is not null)
        {
            value.SyncWeekdayOptions();
            foreach (var option in value.WeekdayOptions) option.PropertyChanged += OnWeekdayChanged;
        }
    }

    /// <summary>星期勾选变化：写回任务的星期集合并重排计划（落盘由「保存」按钮负责）。</summary>
    private void OnWeekdayChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WeekdayOption.IsSelected)) return;
        if (_weekdayOwner is not { } task) return;

        task.ApplyWeekdayOptions();
        _scheduler.Refresh();
    }

    /// <summary>任务属性变化：只处理影响调度的属性，即时重排触发计划（落盘由「保存」按钮负责）。</summary>
    private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ScheduledTask.Kind):
            case nameof(ScheduledTask.OnceDate):
            case nameof(ScheduledTask.OnceTime):
            case nameof(ScheduledTask.TimeOfDay):
            case nameof(ScheduledTask.IntervalMinutes):
            case nameof(ScheduledTask.Enabled):
                _scheduler.Refresh();
                UpdateStatus();
                break;
        }
    }

    /// <summary>生成不重复的默认任务名。</summary>
    private string MakeUniqueName()
    {
        const string baseName = "新任务";
        if (Tasks.All(t => t.Name != baseName)) return baseName;

        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} {i}";
            if (Tasks.All(t => t.Name != candidate)) return candidate;
        }
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "save scheduled tasks failed");
        }
    }

    private void UpdateStatus()
    {
        var enabled = Tasks.Count(t => t.Enabled);
        Status = Tasks.Count == 0 ? "还没有定时任务" : $"共 {Tasks.Count} 条 · 已启用 {enabled} 条";
    }
}