// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SoufflePet.Plugins.NapCat;

/// <summary>缓存反查到的引用摘要，供读取远端历史时直接复用。</summary>
internal sealed class NapCatQuoteTracker
{
    /// <summary>引用消息里展示的被引用正文最大字数。</summary>
    private const int QuotedSummaryLength = 60;

    /// <summary>缓存的反查引用摘要条数上限。</summary>
    private const int MaxQuoteLabels = 200;

    private readonly ILogger _logger;
    private readonly NapCatConnection _connection;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, string> _quoteLabels = new(StringComparer.Ordinal);
    private readonly Queue<string> _quoteLabelOrder = new();

    public NapCatQuoteTracker(ILogger logger, NapCatConnection connection)
    {
        _logger = logger;
        _connection = connection;
    }

    /// <summary>用 get_msg 取回被引用消息的发送者与正文摘要；取不到时返回 null。</summary>
    public async Task<NapCatQuotedMessage?> FetchAsync(string quotedId, long selfId)
    {
        var socket = _connection.Socket;
        if (socket is not { State: WebSocketState.Open }) return null;

        var response = await _connection.SendActionAsync(
                socket,
                "get_msg",
                new Dictionary<string, object?> { ["message_id"] = quotedId })
            .ConfigureAwait(false);

        if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;

        var quotedUserId = NapCatMessageParser.GetInt64(data, "user_id");
        if (quotedUserId <= 0 && data.TryGetProperty("sender", out var quotedSender))
            quotedUserId = NapCatMessageParser.GetInt64(quotedSender, "user_id");

        var summary = Summarize(NapCatMessageParser.ParseMessage(data, selfId).Text);

        return new NapCatQuotedMessage(
            quotedUserId > 0 && quotedUserId == selfId,
            NapCatMessageParser.GetSenderName(data, quotedUserId),
            summary);
    }

    /// <summary>
    /// 把被引用消息的正文补进这一行：成功时统一写成「[引用消息(发送者：摘要)]」，
    /// 反查失败时只保留「引用消息」；反查成功时缓存摘要，供后续渲染远端历史使用。
    /// </summary>
    public string BuildEnrichedLine(string quotedId, string line, NapCatQuotedMessage? quoted)
    {
        var label = quoted is { } value
            ? $"[引用消息({value.Sender}：{value.Summary})]"
            : NapCatMessageParser.QuotePlaceholder;

        // 反查成功就把摘要缓存下来，之后读远端历史也能显示引用内容
        if (quoted is not null) RememberQuoteLabel(quotedId, label);

        // 解析阶段留的占位直接替换，避免留下两个引用标记
        return line.Contains(NapCatMessageParser.QuotePlaceholder, StringComparison.Ordinal)
            ? line.Replace(NapCatMessageParser.QuotePlaceholder, label, StringComparison.Ordinal)
            : PrefixQuote(line, label + " ");
    }

    /// <summary>记住已反查到的引用摘要，供后续渲染远端历史时直接使用。</summary>
    public void RememberQuoteLabel(string quotedId, string label)
    {
        lock (_sync)
        {
            if (_quoteLabels.ContainsKey(quotedId)) return;

            _quoteLabels[quotedId] = label;
            _quoteLabelOrder.Enqueue(quotedId);
            while (_quoteLabelOrder.Count > MaxQuoteLabels)
                _quoteLabels.Remove(_quoteLabelOrder.Dequeue());
        }
    }

    /// <summary>取缓存的引用摘要，未反查过时返回 null。</summary>
    public string? TryGetQuoteLabel(string quotedId)
    {
        lock (_sync) return _quoteLabels.GetValueOrDefault(quotedId);
    }

    /// <summary>在「[会话] 发送者：」之后插入引用标记。</summary>
    private static string PrefixQuote(string line, string prefix)
    {
        var index = line.IndexOf('：', StringComparison.Ordinal);
        return index >= 0 ? line[..(index + 1)] + prefix + line[(index + 1)..] : prefix + line;
    }

    /// <summary>把被引用的正文压成一行短摘要，避免长文把历史记录撑爆。</summary>
    private static string Summarize(string text)
    {
        var single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (single.Length == 0) return "[非文本消息]";

        return single.Length <= QuotedSummaryLength ? single : single[..QuotedSummaryLength] + "…";
    }
}
