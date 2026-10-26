// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Threading;
using Serilog;
using SoufflePet.Models;

namespace SoufflePet.Services;

/// <summary>一条到点需要执行的定时任务（交给对话层发起一轮对话）。</summary>
public sealed record ScheduledTaskTrigger(string TaskId, string Name, string Content);

/// <summary>
/// 一条定时任务的调度参数：调用方（对话工具、界面）把外部输入解析成强类型后组装，由
/// <see cref="ScheduledTaskService" /> 落到任务上。未提供的字段（null）保持任务原值，空星期集合表示「每天」。
/// </summary>
/// <param name="Kind">调度方式。</param>
/// <param name="TimeOfDay">每日 / 每周的触发时刻；单次任务用它表示触发时刻。</param>
/// <param name="OnceDate">单次任务的触发日期。</param>
/// <param name="Weekdays">每周任务的星期集合；空集合表示每天。</param>
/// <param name="IntervalMinutes">间隔任务的间隔分钟数。</param>
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
    /// <summary>任务到点事件：订阅方（对话层）负责真正执行；可能来自 UI 线程之外的线程。</summary>
    event EventHandler<ScheduledTaskTrigger>? Triggered;

    /// <summary>执行日志（最新在前），供管理界面查看。</summary>
    ObservableCollection<ScheduledTaskLogEntry> Logs { get; }

    /// <summary>启动调度，并按持久化配置恢复已启用任务的触发计划。</summary>
    void Start();

    /// <summary>任务被增删改后重新布置定时器；间隔任务沿用既有倒计时，不被重置。</summary>
    void Refresh();

    /// <summary>立即执行一条任务（手动「立即执行」）；不影响其原有计划。</summary>
    void RunNow(ScheduledTask task);

    /// <summary>清空执行日志（内存与日志文件）。</summary>
    void ClearLogs();

    /// <summary>列出全部任务（顺带重排一次触发计划），返回当前任务的快照。</summary>
    IReadOnlyList<ScheduledTask> ListScheduledTasks();

    /// <summary>按 id 查找任务（id 对不上时退回唯一的同名任务）；找不到返回 null。</summary>
    ScheduledTask? FindTask(string id);

    /// <summary>新建任务：落到集合、重排触发计划并落盘；返回新任务（含 id）。</summary>
    ScheduledTask CreateScheduledTask(string name, string content, ScheduledTaskSchedule schedule);

    /// <summary>修改任务：只覆盖传入的字段，重排触发计划并落盘；返回改后的任务，找不到返回 null。</summary>
    ScheduledTask? UpdateScheduledTask(
        string id,
        string? name,
        string? content,
        ScheduledTaskSchedule? schedule,
        bool? enabled);

    /// <summary>删除任务：重排触发计划并落盘；返回被删掉的任务，找不到返回 null。</summary>
    ScheduledTask? DeleteScheduledTask(string id);
}

/// <summary>
/// 定时任务调度服务：只用一个 <see cref="DispatcherTimer" />，触发时间按“最近一条到点任务”动态布置，
/// 没有任务或不临近触发时几乎不产生轮询开销；到点后逐条触发，同一时刻多条任务由对话层串行执行（冲突检测见日志）。
/// 任务配置随 SouffleSettings 持久化，重启后由 <see cref="Start" /> 恢复；执行日志同时落在内存与
/// %AppData%\SoufflePet\scheduled-tasks.log，便于排查任务未触发的问题。
/// </summary>
public sealed class ScheduledTaskService : IScheduledTaskService
{
    private static readonly ILogger Logger = Log.ForContext<ScheduledTaskService>();

    /// <summary>定时器最短间隔（到点任务约半秒内被触发）。</summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>定时器最长间隔：超过时先睡这么久再重新校准，兼顾精度与开销（也覆盖休眠唤醒/改系统时钟）。</summary>
    private static readonly TimeSpan MaxInterval = TimeSpan.FromMinutes(30);

    /// <summary>内存中保留的日志条数上限。</summary>
    private const int MaxLogEntries = 300;

    /// <summary>日志文件保留的行数上限（启动时顺带裁剪）。</summary>
    private const int MaxLogFileLines = 2000;

    /// <summary>执行日志文件（与长期记忆同一目录，独立于应用日志）。</summary>
    private static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SoufflePet", "scheduled-tasks.log");

    private readonly SouffleSettings _settings;
    private readonly DispatcherTimer _timer;

    private bool _disposed;

    public ScheduledTaskService(SouffleSettings settings)
    {
        _settings = settings;
        _timer = new DispatcherTimer { Interval = MaxInterval };
        _timer.Tick += OnTick;
    }

    /// <inheritdoc />
    public event EventHandler<ScheduledTaskTrigger>? Triggered;

    /// <inheritdoc />
    public ObservableCollection<ScheduledTaskLogEntry> Logs { get; } = [];

    public void Start()
    {
        // 先读历史日志再记录启动行，界面打开即能看到重启前的记录
        LoadRecentLogs();
        Refresh();
        var enabled = _settings.ScheduledTasks.Count(t => t.Enabled);
        AddLog($"调度器已启动，恢复 {enabled} 条启用任务");
        Logger.Information("scheduled task service started, enabled={Enabled}", enabled);
    }

    public void Refresh()
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

            // 间隔任务沿用既有的下次触发时间：Refresh 会被列出任务、打开窗口、任意增删改反复调用，
            // 而间隔任务的 ComputeNext 是「now + 间隔」，每次重算都会把倒计时归零导致永不触发。
            // 新建 / 重新启用 / 改间隔时该字段已由模型清空，会走到下面的重新计算。
            if (task.Kind == ScheduledTaskKind.Interval && task.NextRunAt is not null) continue;

            task.NextRunAt = ComputeNext(task, now);
        }

        ArmTimer();
    }

    public void RunNow(ScheduledTask task)
    {
        if (_disposed) return;

        var now = DateTime.Now;
        task.LastRunAt = now;
        task.NextRunAt = task.Enabled ? ComputeNext(task, now) : null;
        AddLog($"手动执行任务「{task.Name}」");
        SaveSettings();
        ArmTimer();
        Triggered?.Invoke(this, new ScheduledTaskTrigger(task.Id, task.Name, task.Content));
    }

    public void ClearLogs()
    {
        Logs.Clear();
        try
        {
            if (File.Exists(LogFilePath)) File.Delete(LogFilePath);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "clear scheduled task log failed");
        }
    }

    /// <summary>列出全部任务（顺带重排一次触发计划），返回任务快照。</summary>
    public IReadOnlyList<ScheduledTask> ListScheduledTasks()
    {
        RunOnUiThread(Refresh);
        return [.. _settings.ScheduledTasks];
    }

    /// <summary>按 id 查找任务；id 对不上时退回唯一的同名任务。找不到返回 null。</summary>
    public ScheduledTask? FindTask(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var key = id.Trim();
        if (_settings.ScheduledTasks.FirstOrDefault(t => string.Equals(t.Id, key, StringComparison.OrdinalIgnoreCase)) is { } exact)
            return exact;

        var matches = _settings.ScheduledTasks
            .Where(t => string.Equals(t.Name, key, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>新建任务：落到集合、重排触发计划并落盘；返回新任务（含 id）。</summary>
    public ScheduledTask CreateScheduledTask(string name, string content, ScheduledTaskSchedule schedule)
    {
        var task = new ScheduledTask();
        RunOnUiThread(() =>
        {
            task.Name = name;
            task.Content = content;
            ApplySchedule(task, schedule);
            _settings.ScheduledTasks.Add(task);
            Refresh();
        });
        SaveSettings();
        return task;
    }

    /// <summary>修改任务：只覆盖传入的字段，重排触发计划并落盘；返回改后的任务，找不到返回 null。</summary>
    public ScheduledTask? UpdateScheduledTask(
        string id,
        string? name,
        string? content,
        ScheduledTaskSchedule? schedule,
        bool? enabled)
    {
        if (FindTask(id) is not { } task) return null;

        RunOnUiThread(() =>
        {
            ApplyChange(task, name, content, schedule, enabled);
            Refresh();
        });
        SaveSettings();
        return task;
    }

    /// <summary>删除任务：重排触发计划并落盘；返回被删掉的任务，找不到返回 null。</summary>
    public ScheduledTask? DeleteScheduledTask(string id)
    {
        if (FindTask(id) is not { } task) return null;

        RunOnUiThread(() =>
        {
            _settings.ScheduledTasks.Remove(task);
            Refresh();
        });
        SaveSettings();
        return task;
    }

    /// <summary>
    /// 生成一份「改动后」的任务预览（不加入集合、不参与调度、不落盘），供调用方展示将要变成的样子。
    /// <paramref name="basedOn" /> 为 null 表示新建，未提供的字段用 <see cref="ScheduledTask" /> 的默认值。
    /// </summary>
    public static ScheduledTask PreviewTask(
        ScheduledTask? basedOn,
        string? name,
        string? content,
        ScheduledTaskSchedule? schedule,
        bool? enabled)
    {
        var task = basedOn is null ? new ScheduledTask() : Copy(basedOn);
        ApplyChange(task, name, content, schedule, enabled);
        return task;
    }

    /// <summary>把集合改动与调度重排放到 UI 线程执行：集合绑在界面上，且调度用的 DispatcherTimer 只能在 UI 线程操作。</summary>
    private static void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Invoke(action);
    }

    /// <summary>把一次改动写到任务上：只覆盖传入的字段。</summary>
    private static void ApplyChange(
        ScheduledTask task,
        string? name,
        string? content,
        ScheduledTaskSchedule? schedule,
        bool? enabled)
    {
        if (name is { } newName) task.Name = newName;
        if (content is { } newContent) task.Content = newContent;
        if (schedule is { } newSchedule) ApplySchedule(task, newSchedule);
        if (enabled is { } isEnabled) task.Enabled = isEnabled;
    }

    /// <summary>把调度参数写到任务上：只覆盖已提供的字段，未提供的保持原值，空星期集合表示每天。</summary>
    private static void ApplySchedule(ScheduledTask task, ScheduledTaskSchedule schedule)
    {
        task.Kind = schedule.Kind;
        switch (schedule.Kind)
        {
            case ScheduledTaskKind.Once:
                // 重新指定日期或时刻后视为未执行，否则 Completed 会让单次任务不再触发
                if (schedule.OnceDate is not null || schedule.TimeOfDay is not null) task.Completed = false;
                if (schedule.OnceDate is { } onceDate) task.OnceDate = onceDate;
                if (schedule.TimeOfDay is { } onceTime) task.OnceTime = onceTime;
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

    /// <summary>复制一条任务（供预览用；副本没有订阅者，改动它不会触发界面刷新）。</summary>
    private static ScheduledTask Copy(ScheduledTask task) => new()
    {
        Id = task.Id,
        Name = task.Name,
        Enabled = task.Enabled,
        Kind = task.Kind,
        OnceDate = task.OnceDate,
        OnceTime = task.OnceTime,
        TimeOfDay = task.TimeOfDay,
        Weekdays = [.. task.Weekdays],
        IntervalMinutes = task.IntervalMinutes,
        Content = task.Content,
        Completed = task.Completed,
    };

    /// <summary>定时器到点：收集所有到点任务，冲突时记录并交由对话层串行执行。</summary>
    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_disposed) return;

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

        // 触发会改变 LastRunAt / Enabled / Completed，需要落盘（集合变化不会触发设置的 PropertyChanged）
        if (due.Count > 0) SaveSettings();
        ArmTimer();
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
        Triggered?.Invoke(this, new ScheduledTaskTrigger(task.Id, task.Name, task.Content));
    }

    /// <summary>按最近一条到点时间布置定时器；没有待触发任务时停表（零轮询开销）。</summary>
    private void ArmTimer()
    {
        if (_disposed)
        {
            _timer.Stop();
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
            _timer.Stop();
            return;
        }

        var interval = due - DateTime.Now;
        if (interval < MinInterval) interval = MinInterval;
        if (interval > MaxInterval) interval = MaxInterval;
        _timer.Interval = interval;
        _timer.Start();
    }

    /// <summary>计算任务在 <paramref name="reference" /> 之后最近一次触发时间；无后续触发时返回 null。</summary>
    private static DateTime? ComputeNext(ScheduledTask task, DateTime reference)
    {
        switch (task.Kind)
        {
            case ScheduledTaskKind.Once:
                // 单次任务：日期/时刻缺省用今天 09:00；已过期的任务在重启后补触发一次
                if (task.Completed) return null;
                return (task.OnceDate ?? DateTimeOffset.Now).LocalDateTime.Date +
                       (task.OnceTime ?? new TimeSpan(9, 0, 0));

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
        Logs.Insert(0, entry);
        while (Logs.Count > MaxLogEntries) Logs.RemoveAt(Logs.Count - 1);
        AppendLogFile(entry.Text);
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

    /// <summary>启动时把日志文件的最近若干行读进内存（顺便裁剪文件，避免无限增长）。</summary>
    private void LoadRecentLogs()
    {
        try
        {
            if (!File.Exists(LogFilePath)) return;

            var lines = File.ReadAllLines(LogFilePath);
            if (lines.Length == 0) return;

            var recent = lines.Length > MaxLogFileLines ? lines[^MaxLogFileLines..] : lines;
            if (lines.Length > MaxLogFileLines) File.WriteAllLines(LogFilePath, recent);

            // 从最新行往前收集，插入后即为“最新在前”
            for (var i = recent.Length - 1; i >= 0; i--)
            {
                var line = recent[i];
                if (line.Trim().Length == 0) continue;
                Logs.Add(new ScheduledTaskLogEntry(line));
                if (Logs.Count >= MaxLogEntries) break;
            }
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
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}