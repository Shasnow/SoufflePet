// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SoufflePet.Chat;
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

            // 平台消息本身就是一条命令时走命令分支（先校验管理员），不再转给模型。
            if (SlashCommandParser.TryParse(notification.Content, out var input)
                && SlashCommandCatalog.Find(input.Name) is { } command)
            {
                await RunPlatformCommandAsync(session, notification, command).ConfigureAwait(false);
                return;
            }

            var senderName = notification.Sender is { Length: > 0 } name ? name : "未知发送者";
            // display 只给界面用；发给模型的内容用 XML 标记包裹，避免和用户正文混淆
            var where = string.IsNullOrWhiteSpace(notification.ConversationId)
                ? notification.PlatformId
                : $"{notification.PlatformId} · {notification.ConversationId}";
            var display = $"{PlatformNoticePrefix} {where} · {senderName}{FormatMarks(notification)}：" +
                          $"{(notification.Content.Length > 0 ? notification.Content : "（无正文）")}";
            _ = session.EnqueueSystemTurnAsync(BuildPlatformPrompt(notification), display, isPlatformNotice: true);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "route platform notification failed");
        }
    }

    /// <summary>
    /// 执行平台消息里的命令：只有平台插件按自己的配置认可的管理员可以触发
    /// （<see cref="IMessagePlatform.AllowsPlatformCommands" />，默认拒绝），
    /// 且只放行不改变整机安全策略的命令（审批模式只能在桌面端切换）。
    /// 结果写进会话通知气泡，并把回执发回原平台会话。
    /// </summary>
    private async Task RunPlatformCommandAsync(
        ChatSession session,
        PlatformIncomingMessage notification,
        SlashCommandDefinition command)
    {
        var sender = notification.Sender is { Length: > 0 } name ? name : "未知发送者";
        var senderId = string.IsNullOrWhiteSpace(notification.SenderId) ? "<无 id>" : notification.SenderId;

        if (!_platforms.AllowsPlatformCommands(notification.PlatformId, notification.SenderId))
        {
            Logger.Warning(
                "platform slash command rejected platform={Platform} sender={Sender} senderId={SenderId} command={Command}",
                notification.PlatformId, sender, senderId, command.Name);
            await session.AddNoticeAsync(
                $"🛑 已忽略 {sender}（{senderId}）的 {command.ContractName}：该发送者不是平台插件配置的命令管理员。" +
                "可在「插件」页对应平台的设置里添加管理员 id。").ConfigureAwait(false);
            return;
        }

        if (!command.AllowFromPlatform)
        {
            var denied = $"{command.Usage} 只能在桌面端主窗口执行，消息平台里不支持。";
            Logger.Warning(
                "platform slash command denied by policy platform={Platform} sender={Sender} command={Command}",
                notification.PlatformId, sender, command.Name);
            await session.AddNoticeAsync($"🛑 {denied}（来自 {sender}）").ConfigureAwait(false);
            await ReplyToPlatformAsync(notification, denied).ConfigureAwait(false);
            return;
        }

        var result = await SlashCommands.ExecuteAsync(session, notification.Content, fromPlatform: true)
            .ConfigureAwait(false);
        Logger.Information(
            "platform slash command from {Platform} sender={Sender} senderId={SenderId} command={Command} ok={Ok}",
            notification.PlatformId, sender, senderId, result.CommandName, result.Success);
        await ReplyToPlatformAsync(notification, result.Message).ConfigureAwait(false);
    }

    /// <summary>
    /// 把命令结果发回原平台会话。回执失败不影响本地提示，但必须留下日志：
    /// 「平台没接入」「会话 id 为空/不合法」这类失败是以返回值表达的（<see cref="PlatformSendResult.Ok" />），
    /// 只 catch 异常会让它们彻底静默。
    /// </summary>
    private async Task ReplyToPlatformAsync(PlatformIncomingMessage notification, string message)
    {
        if (string.IsNullOrWhiteSpace(notification.ConversationId))
        {
            Logger.Warning(
                "skip platform command receipt: no conversation id platform={Platform}",
                notification.PlatformId);
            return;
        }

        if (string.IsNullOrWhiteSpace(message)) return;

        try
        {
            var result = await _platforms
                .TrySendMessageAsync(notification.PlatformId, notification.ConversationId, message)
                .ConfigureAwait(false);
            if (!result.Ok)
            {
                Logger.Warning(
                    "platform command receipt failed platform={Platform} conversation={Conversation} reason={Reason}",
                    notification.PlatformId,
                    notification.ConversationId,
                    result.Message);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(
                ex,
                "send platform command receipt failed platform={Platform} conversation={Conversation}",
                notification.PlatformId,
                notification.ConversationId);
        }
    }

    /// <summary>
    /// 把平台推送包装成带 XML 标记的系统轮次：上下文与本条内容各占一个标记，
    /// 模型能分清「之前说过什么」与「现在要回应什么」；会话 id 单独给出，回复时原样回传。
    /// 「@ 了你」「引用」这类装饰走属性（<c>mentions_self</c> / <c>quote</c>），不混进正文：
    /// 正文一旦带前缀，<c>/model</c> 之类命令就识别不出来，模型也会把装饰当成用户原话。
    /// </summary>
    private static string BuildPlatformPrompt(PlatformIncomingMessage notification)
    {
        var builder = new StringBuilder();
        builder.Append("<platform_message platform=\"").Append(EscapeAttribute(notification.PlatformId)).Append('"');
        if (notification.ConversationId is { Length: > 0 } conversation)
            builder.Append(" conversation=\"").Append(EscapeAttribute(conversation)).Append('"');
        if (notification.Sender is { Length: > 0 } sender)
            builder.Append(" sender=\"").Append(EscapeAttribute(sender)).Append('"');
        if (notification.MentionsSelf) builder.Append(" mentions_self=\"true\"");
        if (notification.Quote is { Length: > 0 } quote)
            builder.Append(" quote=\"").Append(EscapeAttribute(quote)).Append('"');
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

    /// <summary>
    /// 展示行里的结构化标记：<c>@你</c> / <c>引用 发送者：摘要</c>，没有标记时返回空串。
    /// 只在这里拼给人看，正文与提示词都保持干净。
    /// </summary>
    private static string FormatMarks(PlatformIncomingMessage notification)
    {
        var marks = new List<string>(2);
        if (notification.MentionsSelf) marks.Add("@你");
        if (notification.Quote is { Length: > 0 } quote) marks.Add($"引用 {quote}");
        return marks.Count == 0 ? string.Empty : $"（{string.Join(" · ", marks)}）";
    }

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