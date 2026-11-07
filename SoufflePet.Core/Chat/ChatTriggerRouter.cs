// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SoufflePet.Services;

namespace SoufflePet.Core.Chat;

/// <summary>
/// 把平台消息 / 定时任务 / 桌宠交互（戳一戳、摸头）路由到会话：
/// 平台消息固定进入「平台消息」专用会话，定时任务固定进入「定时任务」专用会话，
/// 桌宠交互进入当前活动会话。React SPA 是唯一界面，所有提醒都由这里消费，不会出现两条链路重复消费。
/// </summary>
public sealed class ChatTriggerRouter : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<ChatTriggerRouter>();

    private const int PreviewLength = 80;
    private const string PlatformNoticePrefix = "[平台消息]";
    private const string SystemPrefix = "[系统消息]";

    private readonly ChatSessionManager _sessions;
    private readonly PlatformService _platforms;
    private readonly IScheduledTaskService _scheduled;
    private bool _disposed;

    public ChatTriggerRouter(
        ChatSessionManager sessions,
        PlatformService platforms,
        IScheduledTaskService scheduled)
    {
        _sessions = sessions;
        _platforms = platforms;
        _scheduled = scheduled;
        _platforms.Notified += OnPlatformNotified;
        _scheduled.Triggered += OnScheduledTriggered;
    }

    /// <summary>戳一戳：发起一轮自主对话。</summary>
    public void WakeByPoke() => _sessions.ActiveSession?.WakeByPokeAsync();

    /// <summary>摸摸头：发起一轮自主对话。</summary>
    public void WakeByHeadPat() => _sessions.ActiveSession?.WakeByHeadPatAsync();

    private void OnPlatformNotified(object? sender, PlatformNotification notification)
    {
        _ = RoutePlatformNoticeAsync(notification);
    }

    /// <summary>平台消息固定投递到「平台消息」专用会话，不切换用户当前活动会话。</summary>
    private async Task RoutePlatformNoticeAsync(PlatformNotification notification)
    {
        try
        {
            if (_disposed) return;

            var session = await _sessions
                .GetOrCreatePlatformInboxAsync(CancellationToken.None)
                .ConfigureAwait(false);

            var senderName = notification.Sender is { Length: > 0 } name ? name : "未知发送者";
            var display = $"{PlatformNoticePrefix} {notification.PlatformId} · {senderName}：{Preview(notification.Content)}";
            var prompt = new StringBuilder()
                .Append(PlatformNoticePrefix)
                .Append($"「{notification.PlatformId}」收到一条新消息（发送者：")
                .Append(senderName).Append("）：\n")
                .Append("<<<\n").Append(notification.Content).Append("\n>>>").ToString();
            _ = session.EnqueueSystemTurnAsync(prompt, display, isPlatformNotice: true);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "route platform notification failed");
        }
    }

    private void OnScheduledTriggered(object? sender, ScheduledTaskTrigger trigger)
    {
        _ = RouteScheduledTaskAsync(trigger);
    }

    /// <summary>定时任务固定投递到「定时任务」专用会话，不切换用户当前活动会话。</summary>
    private async Task RouteScheduledTaskAsync(ScheduledTaskTrigger trigger)
    {
        try
        {
            if (_disposed) return;

            var session = await _sessions
                .GetOrCreateScheduledInboxAsync(CancellationToken.None)
                .ConfigureAwait(false);

            var display = $"⏰ 定时任务「{trigger.Name}」：{Preview(trigger.Content)}";
            var prompt = new StringBuilder()
                .Append(SystemPrefix)
                .Append("你被定时任务「").Append(trigger.Name).Append("」唤醒，请执行以下内容：\n")
                .Append("<<<\n").Append(trigger.Content).Append("\n>>>").ToString();
            _ = session.EnqueueSystemTurnAsync(prompt, display, isPlatformNotice: true);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "route scheduled task failed");
        }
    }

    private static string Preview(string content)
    {
        var singleLine = content.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= PreviewLength ? singleLine : singleLine[..PreviewLength] + "…";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _platforms.Notified -= OnPlatformNotified;
        _scheduled.Triggered -= OnScheduledTriggered;
    }
}