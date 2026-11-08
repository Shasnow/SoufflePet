// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Serilog;
using SoufflePet.Models;

namespace SoufflePet.Services;

/// <summary>一条到点需要执行的定时任务（交给对话层发起一轮对话）。</summary>
public sealed record ScheduledTaskTrigger(
    string TaskId,
    string Name,
    string Content,
    string? WorkspacePath = null,
    string? ModelKey = null);

/// <summary>
/// 一条定时任务的调度参数：调用方（对话工具、界面）把外部输入解析成强类型后组装，由
/// <see cref="ScheduledTaskService" /> 落到任务上。未提供的字段（null）保持任务原值，空星期集合表示「每天」。
/// </summary>
public sealed record ScheduledTaskSchedule(
    ScheduledTaskKind Kind,
    TimeSpan? TimeOfDay = null,
    DateTimeOffset? OnceDate = null,
    IReadOnlyList<DayOfWeek>? Weekdays = null,
    int? IntervalMinutes = null);

/// <summary>执行日志的一条记录：<see cref="Text" /> 为界面展示文本，从文件恢复的历史行直接使用原文。</summary>
public sealed record ScheduledTaskLogEntry(string Text, bool IsError = false);

/// <summary>定时任务调度服务的抽象（DI 注册用）。</summary>
public interface IScheduledTaskService : IDisposable
{
    /// <summary>任务到点事件：订阅方（对话层）负责真正执行；在 UI 线程触发。</summary>
    event EventHandler<ScheduledTaskTrigger>? Triggered;

    /// <summary>执行日志快照（最新在前）。</summary>
    IReadOnlyList<ScheduledTaskLogEntry> Logs { get; }

    /// <summary>日志变化（UI 侧重新投影到 ObservableCollection）。</summary>
    event EventHandler? LogsChanged;

    void Start();
    void Refresh();
    void RunNow(ScheduledTask task);
    void ClearLogs();
    IReadOnlyList<ScheduledTask> ListScheduledTasks();
    ScheduledTask? FindTask(string id);
    ScheduledTask CreateScheduledTask(
        string name,
        string content,
        ScheduledTaskSchedule schedule,
        string? workspacePath = null,
        string? modelKey = null);
    ScheduledTask? UpdateScheduledTask(
        string id,
        string? name,
        string? content,
        ScheduledTaskSchedule? schedule,
        bool? enabled,
        string? workspacePath = null,
        string? modelKey = null);
    ScheduledTask? DeleteScheduledTask(string id);
}

/// <summary>
/// 定时任务调度服务：用 System.Threading.Timer 做一次性延时，到点后经 IUiDispatcher 回到 UI 线程
/// 执行（Core 不引用 Avalonia）。任务配置随 SouffleSettings 持久化，执行日志落在
/// <c>~/.souffle/scheduled-tasks.log</c>。
/// </summary>
public sealed class ScheduledTaskService : IScheduledTaskService
{
    private static readonly ILogger Logger = Log.ForContext<ScheduledTaskService>();

    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxInterval = TimeSpan.FromMinutes(30);

    private const int MaxLogEntries = 300;
    private const int MaxLogFileLines = 2000;

    private static readonly string LogFilePath = Path.Combine(
        ChatMemory.SouffleHome, "scheduled-tasks.log");

    private readonly SouffleSettings _settings;
    private readonly IUiDispatcher _ui;
    private readonly Timer _timer;
    private readonly Lock _gate = new();
    private readonly List<ScheduledTaskLogEntry> _logs = [];

    private bool _disposed;

    public ScheduledTaskService(SouffleSettings settings, IUiDispatcher ui)
    {
        _settings = settings;
        _ui = ui;
        _timer = new Timer(_ => OnTimer(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event EventHandler<ScheduledTaskTrigger>? Triggered;

    public event EventHandler? LogsChanged;

    public IReadOnlyList<ScheduledTaskLogEntry> Logs
    {
        get
        {
            lock (_gate) return [.. _logs];
        }
    }

    public void Start()
    {
        RunOnUiThread(() =>
        {
            LoadRecentLogs();
            RefreshCore();
        });
        var enabled = _settings.ScheduledTasks.Count(t => t.Enabled);
        Logger.Information("scheduled task service started, enabled={Enabled}", enabled);
    }

    public void Refresh() => RunOnUiThread(RefreshCore);

    public void RunNow(ScheduledTask task)
    {
        if (_disposed) return;
        RunOnUiThread(() =>
        {
            var now = DateTime.Now;
            task.LastRunAt = now;
            task.NextRunAt = task.Enabled ? ComputeNext(task, now) : null;
            AddLog($"手动执行任务「{task.Name}」");
            SaveSettings();
            ArmTimerCore();
            Triggered?.Invoke(this, new ScheduledTaskTrigger(
                task.Id, task.Name, task.Content, task.WorkspacePath, task.ModelKey));
        });
    }

    public void ClearLogs()
    {
        RunOnUiThread(() =>
        {
            lock (_gate) _logs.Clear();
            try
            {
                if (File.Exists(LogFilePath)) File.Delete(LogFilePath);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "clear scheduled task log failed");
            }

            LogsChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public IReadOnlyList<ScheduledTask> ListScheduledTasks()
    {
        IReadOnlyList<ScheduledTask> result = [];
        RunOnUiThread(() =>
        {
            RefreshCore();
            result = [.. _settings.ScheduledTasks];
        });
        return result;
    }

    public ScheduledTask? FindTask(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        ScheduledTask? result = null;
        RunOnUiThread(() => result = FindTaskCore(id));
        return result;
    }

    public ScheduledTask CreateScheduledTask(
        string name,
        string content,
        ScheduledTaskSchedule schedule,
        string? workspacePath = null,
        string? modelKey = null)
    {
        var task = new ScheduledTask();
        RunOnUiThread(() =>
        {
            task.Name = name;
            task.Content = content;
            ApplyRoute(task, workspacePath, modelKey);
            ApplySchedule(task, schedule);
            _settings.ScheduledTasks.Add(task);
            RefreshCore();
        });
        SaveSettings();
        return task;
    }

    public ScheduledTask? UpdateScheduledTask(
        string id,
        string? name,
        string? content,
        ScheduledTaskSchedule? schedule,
        bool? enabled,
        string? workspacePath = null,
        string? modelKey = null)
    {
        ScheduledTask? result = null;
        RunOnUiThread(() =>
        {
            if (FindTaskCore(id) is not { } task) return;
            ApplyChange(task, name, content, schedule, enabled, workspacePath, modelKey);
            RefreshCore();
            result = task;
        });
        if (result is not null) SaveSettings();
        return result;
    }

    public ScheduledTask? DeleteScheduledTask(string id)
    {
        ScheduledTask? result = null;
        RunOnUiThread(() =>
        {
            if (FindTaskCore(id) is not { } task) return;
            _settings.ScheduledTasks.Remove(task);
            RefreshCore();
            result = task;
        });
        if (result is not null) SaveSettings();
        return result;
    }

    /// <summary>
    /// 生成一份「改动后」的任务预览（不加入集合、不参与调度、不落盘），供调用方展示将要变成的样子。
    /// </summary>
    public static ScheduledTask PreviewTask(
        ScheduledTask? basedOn,
        string? name,
        string? content,
        ScheduledTaskSchedule? schedule,
        bool? enabled)
    {
        var task = basedOn is null ? new ScheduledTask() : Copy(basedOn);
        ApplyChange(task, name, content, schedule, enabled, null, null);
        return task;
    }

    private ScheduledTask? FindTaskCore(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var key = id.Trim();
        if (_settings.ScheduledTasks.FirstOrDefault(t =>
                string.Equals(t.Id, key, StringComparison.OrdinalIgnoreCase)) is { } exact)
            return exact;

        var matches = _settings.ScheduledTasks
            .Where(t => string.Equals(t.Name, key, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private void RunOnUiThread(Action action)
    {
        if (_ui.IsUiThread) action();
        else _ui.Invoke(action);
    }

    private static void ApplyChange(
        ScheduledTask task,
        string? name,
        string? content,
        ScheduledTaskSchedule? schedule,
        bool? enabled,
        string? workspacePath,
        string? modelKey)
    {
        if (name is { } newName) task.Name = newName;
        if (content is { } newContent) task.Content = newContent;
        if (schedule is { } newSchedule) ApplySchedule(task, newSchedule);
        if (enabled is { } isEnabled) task.Enabled = isEnabled;
        ApplyRoute(task, workspacePath, modelKey);
    }

    /// <summary>null 表示保持原值，空字符串表示清空（投递到全局定时任务会话 / 沿用会话模型）。</summary>
    private static void ApplyRoute(ScheduledTask task, string? workspacePath, string? modelKey)
    {
        if (workspacePath is { } path) task.WorkspacePath = NullIfBlank(path);
        if (modelKey is { } key) task.ModelKey = NullIfBlank(key);
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ApplySchedule(ScheduledTask task, ScheduledTaskSchedule schedule)
    {
        task.Kind = schedule.Kind;
        switch (schedule.Kind)
        {
            case ScheduledTaskKind.Once:
                if (schedule.OnceDate is not null || schedule.TimeOfDay is not null) task.Completed = false;
                if (schedule.OnceDate is { } onceDate) task.OnceDate = onceDate;
                if (schedule.TimeOfDay is { } onceTime) task.TimeOfDay = onceTime;
                break;
            case ScheduledTaskKind.Daily:
                if (schedule.TimeOfDay is { } dailyTime) task.TimeOfDay = dailyTime;
                break;
            case ScheduledTaskKind.Weekly:
                if (schedule.TimeOfDay is { } weeklyTime) task.TimeOfDay = weeklyTime;
                if (schedule.Weekdays is { } weekdays)
                {
                    task.Weekdays = [.. weekdays];
                    task.SyncWeekdayOptions();
                }
                break;
            case ScheduledTaskKind.Interval:
                if (schedule.IntervalMinutes is { } minutes) task.IntervalMinutes = minutes;
                break;
        }
    }

    private static ScheduledTask Copy(ScheduledTask task) => new()
    {
        Id = task.Id,
        Name = task.Name,
        Enabled = task.Enabled,
        Kind = task.Kind,
        OnceDate = task.OnceDate,
        TimeOfDay = task.TimeOfDay,
        Weekdays = [.. task.Weekdays],
        IntervalMinutes = task.IntervalMinutes,
        Content = task.Content,
        WorkspacePath = task.WorkspacePath,
        ModelKey = task.ModelKey,
        Completed = task.Completed,
    };

    private void RefreshCore()
    {
        if (_disposed) return;

        var now = DateTime.Now;
        foreach (var task in _settings.ScheduledTasks)
        {
            if (!task.Enabled)
            {
                task.NextRunAt = null;
                continue;
            }

            // 间隔任务沿用既有倒计时：Refresh 会被列出任务、打开窗口、任意增删改反复调用。
            if (task.Kind == ScheduledTaskKind.Interval && task.NextRunAt is not null) continue;
            task.NextRunAt = ComputeNext(task, now);
        }

        ArmTimerCore();
    }

    private void OnTimer()
    {
        if (_disposed) return;
        _ui.Post(OnTick);
    }

    private void OnTick()
    {
        if (_disposed) return;
        ArmTimerCore(); // 先停表，计算完再按下次时间重排

        var now = DateTime.Now;
        List<ScheduledTask> due = [];
        foreach (var task in _settings.ScheduledTasks)
        {
            if (!task.Enabled || task.NextRunAt is not { } next) continue;
            if (next <= now) due.Add(task);
        }

        if (due.Count > 1)
            AddLog($"检测到 {due.Count} 条任务同时到点（{string.Join("、", due.Select(t => t.Name))}），将按顺序串行执行");

        try
        {
            foreach (var task in due) Fire(task, now);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "scheduled task tick failed");
            AddLog($"调度出错：{ex.Message}", isError: true);
        }

        if (due.Count > 0) SaveSettings();
        ArmTimerCore();
    }

    private void Fire(ScheduledTask task, DateTime now)
    {
        task.LastRunAt = now;
        if (task.Kind == ScheduledTaskKind.Once)
        {
            task.Completed = true;
            task.Enabled = false;
        }

        task.NextRunAt = ComputeNext(task, now);
        var nextText = task.NextRunAt is { } next ? next.ToString("yyyy-MM-dd HH:mm") : "无（不再重复）";
        AddLog($"任务「{task.Name}」已触发，交给桌宠执行（下次 {nextText}）");
        Logger.Information("scheduled task fired {Name} kind={Kind}", task.Name, task.Kind);
        Triggered?.Invoke(this, new ScheduledTaskTrigger(
            task.Id, task.Name, task.Content, task.WorkspacePath, task.ModelKey));
    }

    private void ArmTimerCore()
    {
        if (_disposed)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        DateTime? earliest = null;
        foreach (var task in _settings.ScheduledTasks)
        {
            if (!task.Enabled || task.NextRunAt is not { } next) continue;
            if (earliest is null || next < earliest) earliest = next;
        }

        if (earliest is not { } due)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        var interval = due - DateTime.Now;
        if (interval < MinInterval) interval = MinInterval;
        if (interval > MaxInterval) interval = MaxInterval;
        _timer.Change(interval, Timeout.InfiniteTimeSpan);
    }

    /// <summary>计算任务在 reference 之后最近一次触发时间；无后续触发时返回 null（测试可见）。</summary>
    internal static DateTime? ComputeNext(ScheduledTask task, DateTime reference)
    {
        switch (task.Kind)
        {
            case ScheduledTaskKind.Once:
                if (task.Completed) return null;
                return (task.OnceDate ?? DateTimeOffset.Now).LocalDateTime.Date +
                       (task.TimeOfDay ?? new TimeSpan(9, 0, 0));

            case ScheduledTaskKind.Daily:
                return NextDaily(reference, task.TimeOfDay);

            case ScheduledTaskKind.Weekly:
            {
                if (task.Weekdays.Count == 0) return NextDaily(reference, task.TimeOfDay);
                var time = task.TimeOfDay ?? new TimeSpan(9, 0, 0);
                for (var i = 0; i <= 7; i++)
                {
                    var day = reference.Date.AddDays(i);
                    if (!task.Weekdays.Contains(day.DayOfWeek)) continue;
                    var candidate = day + time;
                    if (candidate > reference) return candidate;
                }

                return null;
            }

            case ScheduledTaskKind.Interval:
                return reference.AddMinutes(Math.Max(1, task.IntervalMinutes));

            default:
                return null;
        }
    }

    private static DateTime NextDaily(DateTime reference, TimeSpan? timeOfDay)
    {
        var candidate = reference.Date + (timeOfDay ?? new TimeSpan(9, 0, 0));
        return candidate <= reference ? candidate.AddDays(1) : candidate;
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "save settings after scheduled task change failed");
        }
    }

    private void AddLog(string message, bool isError = false)
    {
        var entry = new ScheduledTaskLogEntry($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}", isError);
        lock (_gate)
        {
            _logs.Insert(0, entry);
            while (_logs.Count > MaxLogEntries) _logs.RemoveAt(_logs.Count - 1);
        }

        AppendLogFile(entry.Text);
        LogsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void AppendLogFile(string line)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.AppendAllText(LogFilePath, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "append scheduled task log failed");
        }
    }

    private void LoadRecentLogs()
    {
        try
        {
            lock (_gate) _logs.Clear();
            if (!File.Exists(LogFilePath)) return;

            var lines = File.ReadAllLines(LogFilePath);
            if (lines.Length == 0) return;

            var recent = lines.Length > MaxLogFileLines ? lines[^MaxLogFileLines..] : lines;
            if (lines.Length > MaxLogFileLines) File.WriteAllLines(LogFilePath, recent);

            lock (_gate)
            {
                for (var i = recent.Length - 1; i >= 0; i--)
                {
                    var line = recent[i];
                    if (line.Trim().Length == 0) continue;
                    _logs.Add(new ScheduledTaskLogEntry(line));
                    if (_logs.Count >= MaxLogEntries) break;
                }
            }

            LogsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "load scheduled task logs failed");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _timer.Dispose();
    }
}