// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.ComponentModel;
using System.Globalization;
using System.Text;
using SoufflePet.Models;

namespace SoufflePet.Services;

/// <summary>
/// 定时任务的列出、新建、修改、删除与调度参数解析。
/// </summary>
public sealed partial class ChatTools
{
    /// <summary>列出全部定时任务（数据取自调度服务，这里只负责转成可读文本）。</summary>
    private string ListScheduledTasks()
    {
        var tasks = _scheduledTasks.ListScheduledTasks();
        if (tasks.Count == 0) return "当前没有任何定时任务。";

        var builder = new StringBuilder($"共 {tasks.Count} 条定时任务：");
        foreach (var task in tasks)
        {
            builder.Append("\n- id=").Append(task.Id)
                .Append(" 名称「").Append(task.Name).Append('」')
                .Append(" 状态=").Append(task.Enabled ? "启用" : "停用")
                .Append(" 计划=").Append(task.ScheduleText)
                .Append(" 下次触发=").Append(task.NextRunText)
                .Append(" 内容=").Append(ContentPreview(task.Content));
        }
        return builder.ToString();
    }

    /// <summary>新建定时任务：校验参数后交给调度服务落盘。</summary>
    private async Task<string> CreateScheduledTask(
        [Description("任务名称，例如「喝水提醒」")] string name,
        [Description("任务内容，例如「提醒我该喝水了」")] string content,
        [Description("调度方式：once=单次、daily=每日、weekly=每周、interval=固定间隔；不传按单次处理")] string? kind = null,
        [Description("触发时刻，24 小时制 HH:mm，例如 09:30；interval 方式不需要")] string? time = null,
        [Description("once 方式的触发日期 yyyy-MM-dd，例如 2026-10-27；不传表示今天")] string? date = null,
        [Description("weekly 方式选中的星期，逗号分隔，1=周一 … 7=周日，例如 \"1,3,5\"；留空表示每天")] string? weekdays = null,
        [Description("interval 方式的间隔分钟数，1-10080；不传默认 60")] int? intervalMinutes = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "任务内容不能为空";
        if (!TryParseKind(kind, out var parsedKind)) return UnrecognizedKind(kind);
        if (!TryBuildSchedule(parsedKind, time, date, weekdays, intervalMinutes, out var schedule, out var error))
            return error;

        var taskName = string.IsNullOrWhiteSpace(name) ? "新任务" : name.Trim();
        var taskContent = content.Trim();
        var preview = ScheduledTaskService.PreviewTask(null, taskName, taskContent, schedule, enabled: null);
        var detail = $"新建任务「{preview.Name}」（{preview.ScheduleText}）\n内容：{ContentPreview(preview.Content)}";
        if (!await _access.RequestApprovalAsync("create_scheduled_task", detail, cancellationToken).ConfigureAwait(false))
            return ApprovalDenied(cancellationToken, "新建定时任务");

        var created = _scheduledTasks.CreateScheduledTask(taskName, taskContent, schedule);
        return $"已新建定时任务「{created.Name}」（{created.ScheduleText}），下次触发 {created.NextRunText}。";
    }

    /// <summary>修改定时任务：校验参数后交给调度服务落盘（只改传入的字段）。</summary>
    private async Task<string> UpdateScheduledTask(
        [Description("要修改的任务 id")] string id,
        [Description("新的任务名称；不传保持原样")] string? name = null,
        [Description("新的任务内容；不传保持原样")] string? content = null,
        [Description("新的调度方式：once/daily/weekly/interval；不传保持原样")] string? kind = null,
        [Description("新的触发时刻，HH:mm；不传保持原样")] string? time = null,
        [Description("once 方式的新触发日期 yyyy-MM-dd；不传保持原样")] string? date = null,
        [Description("weekly 方式新的星期，例如 \"1,3,5\"；传空字符串表示每天；不传保持原样")] string? weekdays = null,
        [Description("interval 方式新的间隔分钟数，1-10080；不传保持原样")] int? intervalMinutes = null,
        [Description("是否启用；不传保持原样")] bool? enabled = null,
        CancellationToken cancellationToken = default)
    {
        if (_scheduledTasks.FindTask(id) is not { } task) return TaskMissing(id);

        var targetKind = task.Kind;
        if (kind is not null && !TryParseKind(kind, out targetKind)) return UnrecognizedKind(kind);

        ScheduledTaskSchedule? schedule = null;
        if (kind is not null || time is not null || date is not null || weekdays is not null || intervalMinutes is not null)
        {
            if (!TryBuildSchedule(targetKind, time, date, weekdays, intervalMinutes, out var parsed, out var error))
                return error;
            schedule = parsed;
        }

        var newName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        var newContent = string.IsNullOrWhiteSpace(content) ? null : content.Trim();
        if (newName is null && newContent is null && schedule is null && enabled is null)
            return "没有给出任何要修改的内容：请至少提供名称、内容、调度或启用状态中的一项。";

        var preview = ScheduledTaskService.PreviewTask(task, newName, newContent, schedule, enabled);
        var detail = $"修改任务「{task.Name}」\n当前：{task.ScheduleText}，{(task.Enabled ? "启用" : "停用")}\n"
            + $"改动后：{preview.ScheduleText}，{(preview.Enabled ? "启用" : "停用")}\n内容：{ContentPreview(preview.Content)}";
        if (!await _access.RequestApprovalAsync("update_scheduled_task", detail, cancellationToken).ConfigureAwait(false))
            return ApprovalDenied(cancellationToken, "修改定时任务");

        return _scheduledTasks.UpdateScheduledTask(task.Id, newName, newContent, schedule, enabled) is { } updated
            ? $"已修改定时任务「{updated.Name}」（{updated.ScheduleText}），下次触发 {updated.NextRunText}。"
            : TaskMissing(id);
    }

    /// <summary>删除定时任务：先查后交给调度服务删除。</summary>
    private async Task<string> DeleteScheduledTask(
        [Description("要删除的任务 id")] string id,
        CancellationToken cancellationToken = default)
    {
        if (_scheduledTasks.FindTask(id) is not { } task) return TaskMissing(id);

        var detail = $"删除任务「{task.Name}」（{task.ScheduleText}）\n内容：{ContentPreview(task.Content)}";
        if (!await _access.RequestApprovalAsync("delete_scheduled_task", detail, cancellationToken).ConfigureAwait(false))
            return ApprovalDenied(cancellationToken, "删除定时任务");

        return _scheduledTasks.DeleteScheduledTask(task.Id) is { } removed
            ? $"已删除定时任务「{removed.Name}」。"
            : TaskMissing(id);
    }

    /// <summary>校验并把模型的调度参数组装成强类型对象：全部参数解析成功才返回。</summary>
    private static bool TryBuildSchedule(
        ScheduledTaskKind kind, string? time, string? date, string? weekdays, int? intervalMinutes,
        out ScheduledTaskSchedule schedule, out string error)
    {
        schedule = new ScheduledTaskSchedule(kind);
        error = string.Empty;

        TimeSpan? parsedTime = null;
        if (time is not null)
        {
            if (!TryParseTimeOfDay(time, out var timeValue))
            {
                error = $"触发时刻「{time}」无法识别，请用 24 小时制 HH:mm，例如 09:30。";
                return false;
            }
            parsedTime = timeValue;
        }

        DateTimeOffset? parsedDate = null;
        if (date is not null)
        {
            if (!TryParseDate(date, out var dateValue))
            {
                error = $"日期「{date}」无法识别，请用 yyyy-MM-dd，例如 2026-10-27。";
                return false;
            }
            parsedDate = dateValue;
        }

        List<DayOfWeek>? parsedWeekdays = null;
        if (weekdays is not null)
        {
            if (!TryParseWeekdays(weekdays, out var daysValue))
            {
                error = $"星期「{weekdays}」无法识别，请用逗号分隔的 1-7（1=周一 … 7=周日），例如 \"1,3,5\"。";
                return false;
            }
            parsedWeekdays = daysValue;
        }

        int? parsedInterval = null;
        if (intervalMinutes is { } minutes)
        {
            if (minutes is < MinTaskIntervalMinutes or > MaxTaskIntervalMinutes)
            {
                error = $"间隔分钟数 {minutes} 超出范围（{MinTaskIntervalMinutes}-{MaxTaskIntervalMinutes}）。";
                return false;
            }
            parsedInterval = minutes;
        }

        schedule = new ScheduledTaskSchedule(kind, parsedTime, parsedDate, parsedWeekdays, parsedInterval);
        return true;
    }

    /// <summary>解析调度方式（中英文别名）；未提供（null / 空）时按默认的单次任务处理。</summary>
    private static bool TryParseKind(string? kind, out ScheduledTaskKind result)
    {
        result = ScheduledTaskKind.Once;
        switch (kind?.Trim().ToLowerInvariant())
        {
            case null:
            case "":
            case "default":
            case "once":
            case "single":
            case "单次":
                result = ScheduledTaskKind.Once;
                return true;
            case "daily":
            case "day":
            case "每日":
            case "每天":
                result = ScheduledTaskKind.Daily;
                return true;
            case "weekly":
            case "week":
            case "每周":
                result = ScheduledTaskKind.Weekly;
                return true;
            case "interval":
            case "repeat":
            case "间隔":
            case "循环":
                result = ScheduledTaskKind.Interval;
                return true;
            default:
                return false;
        }
    }

    /// <summary>解析触发时刻（hh:mm / h:mm / hh:mm:ss）。</summary>
    private static bool TryParseTimeOfDay(string time, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (TimeSpan.TryParseExact(time.Trim(), [@"hh\:mm", @"h\:mm", @"hh\:mm\:ss", @"h\:mm\:ss"], CultureInfo.InvariantCulture, out var parsed)
            && parsed >= TimeSpan.Zero && parsed < TimeSpan.FromDays(1))
        {
            result = parsed;
            return true;
        }
        return false;
    }

    /// <summary>解析单次任务的日期（yyyy-MM-dd）。</summary>
    private static bool TryParseDate(string date, out DateTimeOffset result)
    {
        if (DateTimeOffset.TryParseExact(date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            result = parsed;
            return true;
        }
        result = default;
        return false;
    }

    /// <summary>解析星期（1=周一 … 7=周日，逗号/顿号/分隔符分隔；空字符串表示每天）。</summary>
    private static bool TryParseWeekdays(string weekdays, out List<DayOfWeek> result)
    {
        result = [];
        var trimmed = weekdays.Trim();
        if (trimmed.Length == 0) return true;

        foreach (var token in trimmed.Split([',', '，', '、', '/', ';', '；', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(token.Trim(), out var number) || number is < 1 or > 7) return false;
            var day = number == 7 ? DayOfWeek.Sunday : (DayOfWeek)number;
            if (!result.Contains(day)) result.Add(day);
        }
        return result.Count > 0;
    }

    /// <summary>把任务内容压成单行预览，供审批与结果转述。</summary>
    private static string ContentPreview(string content)
    {
        var singleLine = content.ReplaceLineEndings(" ").Trim();
        return singleLine.Length == 0 ? "(空)" : Truncate(singleLine, TaskContentPreviewLength);
    }

    private static string ApprovalDenied(CancellationToken cancellationToken, string action) =>
        cancellationToken.IsCancellationRequested
            ? $"{action}已取消。"
            : $"用户拒绝{action}，不要重试，改为告知用户已被拒绝。";

    private static string TaskMissing(string id) =>
        $"没有找到 id 为「{id}」的定时任务，请先用 list_scheduled_tasks 确认。";

    private static string UnrecognizedKind(string? kind) =>
        $"无法识别的调度方式「{kind}」，请用 once / daily / weekly / interval。";
}
