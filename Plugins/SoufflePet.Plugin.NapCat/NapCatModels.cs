// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;

namespace SoufflePet.Plugins.NapCat;

/// <summary>
/// 解析出的消息：<see cref="Text" /> 是**纯正文**（只有对方写的内容与图片/文件/合并转发占位），
/// 「@ 了桌宠」「引用了某条消息」分别用 <see cref="MentionsSelf" /> / <see cref="QuotedId" /> 表达，
/// 不再拼进正文——宿主靠「正文是否以 / 开头」识别命令，前缀会让命令永远不命中。
/// </summary>
internal sealed record NapCatParsedMessage(
    string Text,
    bool MentionsSelf,
    string? QuotedId,
    IReadOnlyList<NapCatPendingAttachment>? Attachments = null,
    IReadOnlyList<NapCatPendingForward>? Forwards = null);

/// <summary>正文里的附件占位符、下载地址，以及下载失败时退回的文本。</summary>
internal sealed record NapCatPendingAttachment(
    string Placeholder,
    string Url,
    string Label,
    string Prefix,
    string? FileName,
    string Fallback);

/// <summary>正文里的合并转发占位符、合并转发 id，以及反查失败时退回的文本。</summary>
internal sealed record NapCatPendingForward(string Placeholder, string ForwardId, string Fallback);

/// <summary>被引用消息的摘要信息。</summary>
internal sealed record NapCatQuotedMessage(bool IsSelf, string Sender, string Summary);

/// <summary>
/// 会话里的一条消息。<see cref="Text" /> 是纯正文（可能已补上附件路径或合并转发占位），
/// 「@ 了你」「引用了某条消息」是**字段**（<see cref="MentionsSelf" /> / <see cref="QuotedId" /> / <see cref="Quote" />），
/// 不在正文里；「发送者（标记）：正文」这种展示行只在读取侧拼，
/// 记录本身永远不带装饰。消息 id 用于把补全后的正文 / 引用摘要写回同一条记录。
/// </summary>
internal sealed record NapCatStoredMessage(
    string MessageId,
    string Text,
    long UserId,
    string Sender,
    DateTimeOffset ReceivedAt,
    bool MentionsSelf = false,
    string? QuotedId = null,
    string? Quote = null)
{
    /// <summary>渲染成给人 / 模型看的整行；只在读取侧拼接，正文本身不带前缀。</summary>
    public string FormatLine() => Format(Sender, Text, MentionsSelf, Quote, QuotedId is not null);

    /// <summary>
    /// 展示行的唯一格式：读取历史、远端历史、唤醒上下文都用它，避免各处各拼一份。
    ///
    /// **不带会话 id**：一条记录只属于一个会话，读历史 / 唤醒上下文本身就在该会话的上下文里
    ///（<c>&lt;platform_message conversation="…"&gt;</c>、<c>get_platform_messages</c> 的结果头），
    /// 每行再写一遍「[group:1]」只是把同一份结构化信息又抄进正文。
    /// <paramref name="quote" /> 是被引用消息的摘要（「发送者：正文」），
    /// <paramref name="quoted" /> 表示这条确实是回复（摘要没反查到时就只写「引用」）。
    /// </summary>
    public static string Format(
        string sender,
        string text,
        bool mentionsSelf = false,
        string? quote = null,
        bool quoted = false)
    {
        var marks = new List<string>(2);
        if (mentionsSelf) marks.Add("@你");
        if (quote is { Length: > 0 } summary) marks.Add($"引用 {summary}");
        else if (quoted) marks.Add("引用");

        var markText = marks.Count > 0 ? $"（{string.Join(" · ", marks)}）" : string.Empty;
        // 只 @ 了一下（或只引用）的消息正文为空：占位只出现在展示行里
        var body = text.Length > 0 ? text : "（无正文）";
        return $"{sender}{markText}：{body}";
    }
}

/// <summary>落盘的会话缓存。</summary>
internal sealed class NapCatConversationCache
{
    public List<NapCatConversationCacheEntry> Conversations { get; set; } = [];
}

internal sealed class NapCatConversationCacheEntry
{
    public string Id { get; set; } = string.Empty;

    public string? Name { get; set; }

    public DateTimeOffset LastActive { get; set; }

    public List<NapCatStoredMessage> Messages { get; set; } = [];
}
