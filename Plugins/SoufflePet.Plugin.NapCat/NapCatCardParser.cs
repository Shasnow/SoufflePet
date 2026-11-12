// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SoufflePet.Plugins.NapCat;

/// <summary>
/// 卡片消息解析：从 json（群公告、小程序、分享链接、音乐等）与 xml 卡片里提取可读摘要；
/// 取不到内容时退回「[卡片消息]」，避免整张卡片在对话里变成空白。
/// </summary>
internal static class NapCatCardParser
{
    /// <summary>卡片摘要的最大字数，避免长公告撑爆消息正文。</summary>
    private const int MaxSummaryLength = 1000;

    /// <summary>xml 卡片里的标题或摘要字段。</summary>
    private static readonly Regex XmlTitlePattern = new(
        @"<(?:title|summary)>(?:<!\[CDATA\[)?(.*?)(?:\]\]>)?</(?:title|summary)>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>解析数组消息里的 json 卡片消息段。</summary>
    public static string FormatJsonCard(JsonElement data)
        => FormatJsonCard(ReadCardContent(data), null);

    /// <summary>按 CQ 码里的原始 JSON 解析卡片；<paramref name="promptHint" /> 为卡片自带的摘要。</summary>
    public static string FormatJsonCard(string? rawJson, string? promptHint)
        => FormatJsonContent(UnescapeCqArguments(rawJson), promptHint);

    /// <summary>解析数组消息里的 xml 卡片消息段。</summary>
    public static string FormatXmlCard(JsonElement data)
        => FormatXmlCard(ReadCardContent(data));

    /// <summary>按原始 xml 解析卡片：取标题或摘要，拿不到时退回「[卡片消息]」。</summary>
    public static string FormatXmlCard(string? rawXml)
    {
        if (string.IsNullOrWhiteSpace(rawXml)) return "[卡片消息]";

        var match = XmlTitlePattern.Match(UnescapeCqArguments(rawXml));
        var title = match.Success ? Normalize(match.Groups[1].Value) : null;
        return title is null ? "[卡片消息]" : $"[卡片] {title}";
    }

    private static string FormatJsonContent(string? rawJson, string? promptHint)
    {
        var hint = Normalize(promptHint);
        if (string.IsNullOrWhiteSpace(rawJson))
            return hint is null ? "[卡片消息]" : $"[卡片] {hint}";

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return FormatJsonCard(document.RootElement, hint);
        }
        catch (JsonException)
        {
            // 卡片内容不是合法 JSON 时保留已有的摘要，至少让模型知道这里有一张卡片
            return hint is null ? "[卡片消息]" : $"[卡片] {hint}";
        }
    }

    private static string FormatJsonCard(JsonElement root, string? promptHint)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return promptHint is null ? "[卡片消息]" : $"[卡片] {promptHint}";

        // prompt 是平台已经拼好的单行摘要（例如「[群公告]…」），优先使用
        var prompt = Normalize(NapCatMessageParser.GetString(root, "prompt")) ?? promptHint;
        if (prompt is not null) return $"[卡片] {prompt}";

        var (kind, title, description) = ReadMetaSummary(root);
        var parts = new List<string>();
        if (kind is not null) parts.Add(kind);
        if (title is not null) parts.Add(title);
        if (description is not null) parts.Add(description);

        return parts.Count == 0 ? "[卡片消息]" : $"[卡片] {string.Join(' ', parts)}";
    }

    /// <summary>从 meta 里取卡片类型与标题/描述；群公告等卡片的文本是 base64，按 encode 标记解码。</summary>
    private static (string? Kind, string? Title, string? Description) ReadMetaSummary(JsonElement root)
    {
        if (!root.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object)
            return (null, null, null);

        foreach (var item in meta.EnumerateObject())
        {
            var kind = MapKind(item.Name);
            if (item.Value.ValueKind != JsonValueKind.Object) return (kind, null, null);

            return (kind, ReadMetaText(item.Value, "title"), ReadMetaText(item.Value, "desc"));
        }

        return (null, null, null);
    }

    private static string? ReadMetaText(JsonElement meta, string property)
    {
        var value = NapCatMessageParser.GetString(meta, property);
        if (string.IsNullOrWhiteSpace(value)) return null;

        // 群公告等卡片把标题/正文以 base64 存放，并用 encode=1 标记
        if (NapCatMessageParser.GetInt64(meta, "encode") == 1)
            return Normalize(DecodeBase64(value)) ?? Normalize(value);

        return Normalize(value);
    }

    /// <summary>把卡片内部的类型标记翻译成可读名称；未知类型不展示，避免暴露内部字段。</summary>
    private static string? MapKind(string key) => key switch
    {
        "news" or "summary" => "链接",
        "music" => "音乐",
        "video" => "视频",
        "mannounce" => "群公告",
        "detail_1" => "小程序",
        "contact" => "推荐",
        "location" => "位置",
        "share" => "分享",
        _ => null,
    };

    /// <summary>取卡片内容字符串：data.data 通常是 JSON/XML 字符串，个别实现直接给对象。</summary>
    private static string? ReadCardContent(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        if (!data.TryGetProperty("data", out var content)) return null;

        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Object => content.GetRawText(),
            _ => null,
        };
    }

    private static string? DecodeBase64(string text)
    {
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(text));
            return string.IsNullOrWhiteSpace(decoded) ? null : decoded;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>压成单行并按上限截断，避免卡片正文把消息撑得过长。</summary>
    private static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var single = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return single.Length <= MaxSummaryLength ? single : single[..MaxSummaryLength] + "…";
    }

    /// <summary>CQ 码参数里的逗号、方括号和 &amp; 会被转义，解析前先还原。</summary>
    private static string UnescapeCqArguments(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        return value
            .Replace("&#91;", "[", StringComparison.Ordinal)
            .Replace("&#93;", "]", StringComparison.Ordinal)
            .Replace("&#44;", ",", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);
    }
}