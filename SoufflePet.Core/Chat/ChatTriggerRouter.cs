// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SoufflePet.Plugin.Sdk;
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

    private void OnPlatformNotified(object? sender, PlatformIncomingMessage notification)
    {
        _ = RoutePlatformNoticeAsync(notification);
    }

    /// <summary>平台消息固定投递到「平台消息」专用会话，不切换用户当前活动会话。</summary>
    private async Task RoutePlatformNoticeAsync(PlatformIncomingMessage notification)
    {
        try
        {
            if (_disposed) return;

            var session = await _sessions
                .GetOrCreatePlatformInboxAsync(CancellationToken.None)
                .ConfigureAwait(false);

            var senderName = notification.Sender is { Length: > 0 } name ? name : "未知发送者";
            // display 只给界面用；发给模型的内容用 XML 标记包裹，避免和用户正文混淆
            var display = $"{PlatformNoticePrefix} {notification.PlatformId} · {senderName}：{notification.Content}";
            _ = session.EnqueueSystemTurnAsync(BuildPlatformPrompt(notification), display, isPlatformNotice: true);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "route platform notification failed");
        }
    }

    /// <summary>
    /// 把平台推送包装成带 XML 标记的系统轮次：上下文与本条内容各占一个标记，
    /// 模型能分清「之前说过什么」与「现在要回应什么」。
    /// </summary>
    private static string BuildPlatformPrompt(PlatformIncomingMessage notification)
    {
        var builder = new StringBuilder();
        builder.Append("<platform_message platform=\"").Append(EscapeAttribute(notification.PlatformId)).Append('"');
        if (notification.Sender is { Length: > 0 } sender)
            builder.Append(" sender=\"").Append(EscapeAttribute(sender)).Append('"');
        builder.AppendLine(">");
        if (notification.Context is { Length: > 0 } context)
        {
            builder.AppendLine("<context>");
            builder.AppendLine(EscapeText(context));
            builder.AppendLine("</context>");
        }

        builder.AppendLine("<content>");
        builder.AppendLine(EscapeText(notification.Content));
        builder.Append("</content>\n</platform_message>");
        return builder.ToString();
    }

    /// <summary>转义正文里的尖括号，避免平台用户发来的内容提前闭合 XML 标记。</summary>
    private static string EscapeText(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    private void OnScheduledTriggered(object? sender, ScheduledTaskTrigger trigger)
    {
        _ = RouteScheduledTaskAsync(trigger);
    }

    /// <summary>定时任务投递到目标工作区（未指定则全局）的「定时任务」专用会话，不切换用户当前活动会话。</summary>
    private async Task RouteScheduledTaskAsync(ScheduledTaskTrigger trigger)
    {
        try
        {
            if (_disposed) return;

            var session = await _sessions
                .GetOrCreateScheduledInboxAsync(trigger.WorkspacePath, trigger.ModelKey, CancellationToken.None)
                .ConfigureAwait(false);

            var display = $"⏰ 定时任务「{trigger.Name}」：{Preview(trigger.Content)}";
            var prompt = $"<scheduled_task name=\"{EscapeAttribute(trigger.Name)}\">\n" +
                         $"你被定时任务「{trigger.Name}」唤醒，请执行以下内容：\n" +
                         $"{trigger.Content}\n</scheduled_task>";
            _ = session.EnqueueSystemTurnAsync(prompt, display, isPlatformNotice: true);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "route scheduled task failed");
        }
    }

    /// <summary>转义 XML 属性值，避免平台 id / 任务名里的引号或尖括号破坏标记结构。</summary>
    private static string EscapeAttribute(string value) => value
        .Replace("&", "&amp;")
        .Replace("\"", "&quot;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

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