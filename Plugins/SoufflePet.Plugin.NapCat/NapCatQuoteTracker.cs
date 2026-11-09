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

/// <summary>记录自己发出的消息 id 与反查到的引用摘要，用于识别他人引用桌宠消息。</summary>
internal sealed class NapCatQuoteTracker
{
    /// <summary>引用消息里展示的被引用正文最大字数。</summary>
    private const int QuotedSummaryLength = 60;

    /// <summary>记住的自身发送消息 id 数量上限，用于识别他人引用桌宠消息。</summary>
    private const int MaxSentMessageIds = 200;

    /// <summary>缓存的反查引用摘要条数上限。</summary>
    private const int MaxQuoteLabels = 200;

    private readonly ILogger _logger;
    private readonly NapCatConnection _connection;
    private readonly Lock _sync = new();
    private readonly Queue<string> _sentMessageIds = new();
    private readonly HashSet<string> _sentMessageIdSet = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _quoteLabels = new(StringComparer.Ordinal);
    private readonly Queue<string> _quoteLabelOrder = new();

    public NapCatQuoteTracker(ILogger logger, NapCatConnection connection)
    {
        _logger = logger;
        _connection = connection;
    }

    /// <summary>记住自己刚发出的一条消息 id，超出上限时淘汰最早的。</summary>
    public void RememberSentMessageId(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return;

        var id = messageId.Trim();
        lock (_sync)
        {
            if (!_sentMessageIdSet.Add(id)) return;

            _sentMessageIds.Enqueue(id);
            while (_sentMessageIds.Count > MaxSentMessageIds)
                _sentMessageIdSet.Remove(_sentMessageIds.Dequeue());
        }

        _logger.LogDebug("已记录自己发出的消息 id：{Id}", id);
    }

    /// <summary>判断被引用的消息是否为桌宠自己发出的。</summary>
    public bool IsSentBySelf(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return false;

        var id = messageId.Trim();
        lock (_sync) return _sentMessageIdSet.Contains(id);
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

        var summary = Summarize(NapCatMessageParser.ParseMessage(data, selfId, IsSentBySelf).Text);

        return new NapCatQuotedMessage(
            quotedUserId > 0 && quotedUserId == selfId,
            NapCatMessageParser.GetSenderName(data, quotedUserId),
            summary);
    }

    /// <summary>
    /// 把被引用消息的正文补进这一行：成功时写成「（引用 发送者：摘要）」，
    /// 失败时退回「[引用你的消息]」标记；反查成功时缓存摘要，供后续渲染远端历史使用。
    /// </summary>
    public string BuildEnrichedLine(string quotedId, string line, NapCatQuotedMessage? quoted)
    {
        var label = quoted is { } value
            ? value.IsSelf
                ? $"[引用你：{value.Summary}]"
                : $"[引用消息({value.Sender}：{value.Summary})]"
            : IsSentBySelf(quotedId)
                ? "[引用你的消息]"
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
