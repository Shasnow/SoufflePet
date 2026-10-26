// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SoufflePet.Models;

/// <summary>定时任务的调度方式。</summary>
public enum ScheduledTaskKind
{
    /// <summary>单次定时：在指定日期时刻触发一次，触发后自动停用。</summary>
    Once,

    /// <summary>每日定时：每天在指定时刻触发。</summary>
    Daily,

    /// <summary>每周定时：在选中的星期几的指定时刻触发。</summary>
    Weekly,

    /// <summary>自定义间隔：每隔固定分钟数触发一次。</summary>
    Interval
}

/// <summary>周定时的星期可选项：勾选状态由界面写回任务的 <see cref="ScheduledTask.Weekdays" />。</summary>
public partial class WeekdayOption : ObservableObject
{
    public WeekdayOption(DayOfWeek day, string label)
    {
        Day = day;
        Label = label;
    }

    /// <summary>对应的星期。</summary>
    public DayOfWeek Day { get; }

    /// <summary>界面展示的短标签（一 / 二 / …）。</summary>
    public string Label { get; }

    /// <summary>是否被选中。</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// 一条定时任务：触发时把 <see cref="Content" /> 作为提示词交给桌宠发起一轮对话。
/// 随 SoufflePet.json 持久化；<see cref="NextRunAt" /> / <see cref="LastRunAt" /> 为运行态信息，由调度器维护、不落盘。
/// </summary>
public partial class ScheduledTask : ObservableObject
{
    /// <summary>缺省触发时刻（未显式设置时使用）。</summary>
    private static readonly TimeSpan DefaultTimeOfDay = new(9, 0, 0);

    /// <summary>任务标识（创建后不变，用于稳定引用）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>任务名称（展示用）。</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = "新任务";

    /// <summary>是否启用；停用后不再参与调度。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextRunText))]
    public partial bool Enabled { get; set; } = true;

    /// <summary>调度方式。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnce))]
    [NotifyPropertyChangedFor(nameof(IsDaily))]
    [NotifyPropertyChangedFor(nameof(IsWeekly))]
    [NotifyPropertyChangedFor(nameof(IsInterval))]
    [NotifyPropertyChangedFor(nameof(ScheduleText))]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public partial ScheduledTaskKind Kind { get; set; } = ScheduledTaskKind.Daily;

    /// <summary>单次定时的触发日期。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleText))]
    public partial DateTimeOffset? OnceDate { get; set; } = DateTimeOffset.Now.Date;

    /// <summary>单次定时的触发时刻。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleText))]
    public partial TimeSpan? OnceTime { get; set; } = DefaultTimeOfDay;

    /// <summary>每日 / 每周定时的触发时刻。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleText))]
    public partial TimeSpan? TimeOfDay { get; set; } = DefaultTimeOfDay;

    /// <summary>每周定时选中的星期几；为空时按每天处理。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleText))]
    public partial List<DayOfWeek> Weekdays { get; set; } = [];

    /// <summary>自定义间隔的分钟数。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleText))]
    public partial int IntervalMinutes { get; set; } = 60;

    /// <summary>触发时交给桌宠的任务内容（作为提示词发起一轮对话）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContent))]
    [NotifyPropertyChangedFor(nameof(ContentPreview))]
    public partial string Content { get; set; } = string.Empty;

    /// <summary>上次触发时间（本地时间）；不持久化。</summary>
    [ObservableProperty]
    [JsonIgnore]
    public partial DateTime? LastRunAt { get; set; }

    /// <summary>下次预计触发时间（本地时间）；由调度器维护，不持久化。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextRunText))]
    [JsonIgnore]
    public partial DateTime? NextRunAt { get; set; }

    /// <summary>单次任务是否已执行完毕（完成后不再触发）。</summary>
    public bool Completed { get; set; }

    [JsonIgnore] public bool IsOnce => Kind == ScheduledTaskKind.Once;

    [JsonIgnore] public bool IsDaily => Kind == ScheduledTaskKind.Daily;

    [JsonIgnore] public bool IsWeekly => Kind == ScheduledTaskKind.Weekly;

    [JsonIgnore] public bool IsInterval => Kind == ScheduledTaskKind.Interval;

    /// <summary>任务内容是否为空（列表里据此决定是否显示预览行）。</summary>
    [JsonIgnore]
    public bool HasContent => Content.Trim().Length > 0;

    /// <summary>任务内容的单行预览（列表展示用）。</summary>
    [JsonIgnore]
    public string ContentPreview
    {
        get
        {
            var singleLine = Content.ReplaceLineEndings(" ").Trim();
            return singleLine.Length <= 40 ? singleLine : singleLine[..40] + "…";
        }
    }

    /// <summary>调度摘要（列表与编辑区展示用）。</summary>
    [JsonIgnore]
    public string ScheduleText => Kind switch
    {
        ScheduledTaskKind.Once =>
            $"单次 {(OnceDate ?? DateTimeOffset.Now).LocalDateTime:yyyy-MM-dd} {FormatTime(OnceTime)}",
        ScheduledTaskKind.Daily => $"每日 {FormatTime(TimeOfDay)}",
        ScheduledTaskKind.Weekly => Weekdays.Count == 0
            ? $"每周 {FormatTime(TimeOfDay)}（未选星期，按每天）"
            : $"每周 {string.Join("", Weekdays.OrderBy(d => ((int)d + 6) % 7).Select(WeekdayLabel))} {FormatTime(TimeOfDay)}",
        ScheduledTaskKind.Interval => $"每 {Math.Max(1, IntervalMinutes)} 分钟",
        _ => string.Empty
    };

    /// <summary>下次触发时间文案。</summary>
    [JsonIgnore]
    public string NextRunText => NextRunAt is { } next
        ? next.ToString("MM-dd HH:mm")
        : Enabled ? "—" : "已停用";

    /// <summary>周定时的星期选项（固定 7 项，可绑定到界面）。</summary>
    [JsonIgnore]
    public IReadOnlyList<WeekdayOption> WeekdayOptions { get; } =
    [
        new(DayOfWeek.Monday, "一"),
        new(DayOfWeek.Tuesday, "二"),
        new(DayOfWeek.Wednesday, "三"),
        new(DayOfWeek.Thursday, "四"),
        new(DayOfWeek.Friday, "五"),
        new(DayOfWeek.Saturday, "六"),
        new(DayOfWeek.Sunday, "日"),
    ];

    /// <summary>把星期选项的勾选状态同步为当前 <see cref="Weekdays" />（选中任务时由界面调用）。</summary>
    public void SyncWeekdayOptions()
    {
        foreach (var option in WeekdayOptions) option.IsSelected = Weekdays.Contains(option.Day);
    }

    /// <summary>由星期选项的勾选状态反写 <see cref="Weekdays" />（勾选变化时由界面调用）。</summary>
    public void ApplyWeekdayOptions()
    {
        Weekdays = [.. WeekdayOptions.Where(o => o.IsSelected).Select(o => o.Day)];
    }

    private static string FormatTime(TimeSpan? time) => (time ?? DefaultTimeOfDay).ToString(@"hh\:mm");

    private static string WeekdayLabel(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "一",
        DayOfWeek.Tuesday => "二",
        DayOfWeek.Wednesday => "三",
        DayOfWeek.Thursday => "四",
        DayOfWeek.Friday => "五",
        DayOfWeek.Saturday => "六",
        DayOfWeek.Sunday => "日",
        _ => string.Empty
    };
}