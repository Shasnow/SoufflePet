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
    private const int MaxQuotes = 200;

    private readonly ILogger _logger;
    private readonly NapCatConnection _connection;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, string> _quotes = new(StringComparer.Ordinal);
    private readonly Queue<string> _quoteOrder = new();

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

    /// <summary>上报给宿主的引用摘要，同时也是历史里展示的摘要：「发送者：正文摘要」；没反查到内容时返回 null。</summary>
    public static string? FormatQuote(NapCatQuotedMessage? quoted) =>
        quoted is { } value ? $"{value.Sender}：{value.Summary}" : null;

    /// <summary>记住已反查到的引用摘要，供远端历史渲染时直接使用。</summary>
    public void RememberQuote(string quotedId, string quote)
    {
        lock (_sync)
        {
            if (_quotes.ContainsKey(quotedId)) return;

            _quotes[quotedId] = quote;
            _quoteOrder.Enqueue(quotedId);
            while (_quoteOrder.Count > MaxQuotes)
                _quotes.Remove(_quoteOrder.Dequeue());
        }
    }

    /// <summary>取缓存的引用摘要，未反查过时返回 null。</summary>
    public string? TryGetQuote(string quotedId)
    {
        lock (_sync) return _quotes.GetValueOrDefault(quotedId);
    }

    /// <summary>把被引用的正文压成一行短摘要，避免长文把历史记录撑爆。</summary>
    private static string Summarize(string text)
    {
        var single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (single.Length == 0) return "[非文本消息]";

        return single.Length <= QuotedSummaryLength ? single : single[..QuotedSummaryLength] + "…";
    }
}
