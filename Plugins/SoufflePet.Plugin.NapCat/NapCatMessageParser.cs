// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SoufflePet.Plugins.NapCat;

/// <summary>把 OneBot 消息事件解析成可读正文：支持数组段格式，也兼容 string + CQ 码格式。</summary>
internal static class NapCatMessageParser
{
    /// <summary>解析阶段给引用段留的占位，反查拿到正文后原地替换。</summary>
    public const string QuotePlaceholder = "（引用）";

    /// <summary>消息字符串里 OneBot 的 CQ 码，用于解析 message_format=string 的平台。</summary>
    private static readonly Regex CqCodePattern = new(@"\[CQ:([A-Za-z_]+)((?:,[^\]]*)?)\]", RegexOptions.Compiled);

    /// <summary>解析消息正文；<paramref name="isSentBySelf" /> 用于判断被引用消息是否由桌宠自己发出。</summary>
    public static NapCatParsedMessage ParseMessage(
        JsonElement root,
        long selfId,
        Func<string?, bool> isSentBySelf,
        bool collectAttachments = false)
    {
        if (root.TryGetProperty("message", out var message))
        {
            if (message.ValueKind == JsonValueKind.Array)
                return ParseSegments(message, selfId, isSentBySelf, collectAttachments);
            if (message.ValueKind == JsonValueKind.String)
                return ParseCqCode(message.GetString() ?? string.Empty, selfId, isSentBySelf, collectAttachments);
        }

        return ParseCqCode(GetString(root, "raw_message") ?? string.Empty, selfId, isSentBySelf, collectAttachments);
    }

    private static NapCatParsedMessage ParseSegments(
        JsonElement segments,
        long selfId,
        Func<string?, bool> isSentBySelf,
        bool collectAttachments)
    {
        var builder = new StringBuilder();
        var mentionsSelf = false;
        string? quotedId = null;
        List<NapCatPendingAttachment>? attachments = collectAttachments ? [] : null;

        foreach (var segment in segments.EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Object) continue;
            if (!segment.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;

            switch (GetString(segment, "type"))
            {
                case "reply":
                    // 引用的消息是桌宠自己发出的，同样视为在叫桌宠；正文内容稍后用 get_msg 反查补上
                    var id = GetString(data, "id") ?? GetString(data, "message_id");
                    if (string.IsNullOrWhiteSpace(id)) break;

                    // 先用占位表示这是一条引用，读历史时也能看出回复关系
                    builder.Append(QuotePlaceholder);
                    quotedId ??= id;
                    if (isSentBySelf(id)) mentionsSelf = true;

                    break;

                case "text":
                    builder.Append(GetString(data, "text"));
                    break;

                case "at":
                    var qq = GetString(data, "qq");
                    if (qq == "all")
                    {
                        builder.Append("@全体成员");
                    }
                    else if (qq == selfId.ToString(CultureInfo.InvariantCulture))
                    {
                        mentionsSelf = true;
                        builder.Append("@你");
                    }
                    else if (!string.IsNullOrEmpty(qq))
                    {
                        builder.Append('@').Append(qq);
                    }

                    break;

                case "image":
                    AppendAttachment(builder, attachments, "图片", "image",
                        GetString(data, "url"), GetString(data, "file"), "[图片]");
                    break;
                case "face":
                    builder.Append("[表情]");
                    break;
                case "record":
                    builder.Append("[语音]");
                    break;
                case "video":
                    builder.Append("[视频]");
                    break;
                case "file":
                    // 文件消息会把文件名放在 name/file 里，带上名字才知道对方发了什么
                    var fileName = GetString(data, "name") ?? GetString(data, "file");
                    AppendAttachment(builder, attachments, "文件", "file",
                        GetString(data, "url"), fileName, FormatFileSegment(fileName));
                    break;
                case "forward":
                    builder.Append("[合并转发]");
                    break;
                case "json":
                case "xml":
                    builder.Append("[卡片消息]");
                    break;
            }
        }

        return new NapCatParsedMessage(
            builder.ToString().Trim(),
            mentionsSelf,
            quotedId,
            attachments is { Count: > 0 } ? attachments : null);
    }

    /// <summary>解析 CQ 码字符串；非文本段替换成可读占位，@ 自己时记录提醒标记。</summary>
    private static NapCatParsedMessage ParseCqCode(
        string raw,
        long selfId,
        Func<string?, bool> isSentBySelf,
        bool collectAttachments)
    {
        var builder = new StringBuilder();
        var mentionsSelf = false;
        string? quotedId = null;
        List<NapCatPendingAttachment>? attachments = collectAttachments ? [] : null;
        var index = 0;

        foreach (Match match in CqCodePattern.Matches(raw))
        {
            builder.Append(UnescapeCqText(raw[index..match.Index]));
            index = match.Index + match.Length;

            var type = match.Groups[1].Value;
            var arguments = match.Groups[2].Value;
            switch (type)
            {
                case "reply":
                    var id = FindCqArgument(arguments, "id");
                    if (string.IsNullOrWhiteSpace(id)) break;

                    builder.Append(QuotePlaceholder);
                    quotedId ??= id;
                    if (isSentBySelf(id)) mentionsSelf = true;

                    break;

                case "at":
                    var qq = FindCqArgument(arguments, "qq");
                    if (qq == "all")
                    {
                        builder.Append("@全体成员");
                    }
                    else if (qq == selfId.ToString(CultureInfo.InvariantCulture))
                    {
                        mentionsSelf = true;
                        builder.Append("@你");
                    }
                    else if (!string.IsNullOrEmpty(qq))
                    {
                        builder.Append('@').Append(qq);
                    }

                    break;

                case "image":
                    AppendAttachment(builder, attachments, "图片", "image",
                        FindCqArgument(arguments, "url"),
                        FindCqArgument(arguments, "file"),
                        "[图片]");
                    break;
                case "face":
                    builder.Append("[表情]");
                    break;
                case "record":
                    builder.Append("[语音]");
                    break;
                case "video":
                    builder.Append("[视频]");
                    break;
                case "file":
                    var fileName = FindCqArgument(arguments, "name") ?? FindCqArgument(arguments, "file");
                    AppendAttachment(builder, attachments, "文件", "file",
                        FindCqArgument(arguments, "url"), fileName, FormatFileSegment(fileName));
                    break;
                case "forward":
                    builder.Append("[合并转发]");
                    break;
                case "json":
                case "xml":
                    builder.Append("[卡片消息]");
                    break;
            }
        }

        builder.Append(UnescapeCqText(raw[index..]));
        return new NapCatParsedMessage(
            builder.ToString().Trim(),
            mentionsSelf,
            quotedId,
            attachments is { Count: > 0 } ? attachments : null);
    }

    /// <summary>追加一个图片/文件段：可下载时写入占位符并登记下载任务，否则直接写可读占位。</summary>
    private static void AppendAttachment(
        StringBuilder builder,
        List<NapCatPendingAttachment>? attachments,
        string label,
        string prefix,
        string? url,
        string? fileName,
        string fallback)
    {
        var downloadUrl = attachments is null ? null : ResolveDownloadUrl(url);
        if (downloadUrl is null)
        {
            builder.Append(fallback);
            return;
        }

        var placeholder = $"[{label}#{attachments!.Count + 1}]";
        attachments.Add(new NapCatPendingAttachment(placeholder, downloadUrl, label, prefix, fileName, fallback));
        builder.Append(placeholder);
    }

    /// <summary>只接受 http/https 的附件地址；其它情况返回 null。</summary>
    private static string? ResolveDownloadUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsoluteUri
            : null;
    }

    /// <summary>取发送者展示名：优先群名片，其次昵称，最后退回 QQ 号。</summary>
    public static string GetSenderName(JsonElement root, long userId)
    {
        if (root.TryGetProperty("sender", out var sender) && sender.ValueKind == JsonValueKind.Object)
        {
            var card = GetString(sender, "card");
            if (!string.IsNullOrWhiteSpace(card)) return card;

            var nickname = GetString(sender, "nickname");
            if (!string.IsNullOrWhiteSpace(nickname)) return nickname;
        }

        return userId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>把文件段渲染成「[文件 名字]」，拿不到文件名时退回「[文件]」。</summary>
    public static string FormatFileSegment(string? fileName)
    {
        var name = fileName?.Trim();
        return string.IsNullOrEmpty(name) ? "[文件]" : $"[文件 {name}]";
    }

    public static string? GetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    public static long GetInt64(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => 0,
        };
    }

    private static string? FindCqArgument(string arguments, string key)
    {
        var prefix = key + "=";
        foreach (var part in arguments.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var item = part.Trim();
            if (item.StartsWith(prefix, StringComparison.Ordinal)) return item[prefix.Length..];
        }

        return null;
    }

    private static string UnescapeCqText(string text)
    {
        if (text.Length == 0) return text;

        return text
            .Replace("&#91;", "[", StringComparison.Ordinal)
            .Replace("&#93;", "]", StringComparison.Ordinal)
            .Replace("&#44;", ",", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);
    }
}
