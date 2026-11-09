// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SoufflePet.Plugins.NapCat;

/// <summary>用 get_forward_msg 反查合并转发内容，并把正文里的占位符替换成单行摘要。</summary>
internal sealed class NapCatForwardResolver
{
    /// <summary>一条合并转发里最多展示的节点数。</summary>
    private const int MaxNodes = 16;

    /// <summary>单个节点正文的最大字数。</summary>
    private const int MaxNodeTextLength = 64;

    /// <summary>整条摘要的最大字数。</summary>
    private const int MaxSummaryLength = 256;

    private readonly ILogger _logger;
    private readonly NapCatConnection _connection;
    private readonly Func<long> _selfIdProvider;

    public NapCatForwardResolver(ILogger logger, NapCatConnection connection, Func<long> selfIdProvider)
    {
        _logger = logger;
        _connection = connection;
        _selfIdProvider = selfIdProvider;
    }

    /// <summary>把正文里的合并转发占位符替换成内容摘要；反查失败时退回「[合并转发]」。</summary>
    public async Task<string> HydrateAsync(string line, IReadOnlyList<NapCatPendingForward> forwards)
    {
        foreach (var forward in forwards)
        {
            var rendered = await ResolveAsync(forward.ForwardId).ConfigureAwait(false);
            line = line.Replace(
                forward.Placeholder,
                rendered ?? forward.Fallback,
                StringComparison.Ordinal);
        }

        return line;
    }

    /// <summary>把未反查的占位符退回可读文本，避免正文里出现内部占位符。</summary>
    public static string Fallback(string line, IReadOnlyList<NapCatPendingForward> forwards)
    {
        foreach (var forward in forwards)
            line = line.Replace(forward.Placeholder, forward.Fallback, StringComparison.Ordinal);

        return line;
    }

    /// <summary>反查一条合并转发，返回「[合并转发(N条)：发送者：内容；…]」；失败时返回 null。</summary>
    private async Task<string?> ResolveAsync(string forwardId)
    {
        var socket = _connection.Socket;
        if (socket is not { State: WebSocketState.Open }) return null;

        try
        {
            // NapCat 用 message_id 取合并转发，部分实现只认 id，两个都带上以兼容
            var response = await _connection.SendActionAsync(
                    socket,
                    "get_forward_msg",
                    new Dictionary<string, object?>
                    {
                        ["message_id"] = forwardId,
                        ["id"] = forwardId,
                    })
                .ConfigureAwait(false);

            if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return null;
            if (!data.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                return null;

            var selfId = _selfIdProvider();
            var builder = new StringBuilder();
            var nodeCount = 0;
            foreach (var node in messages.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object) continue;

                var text = Summarize(NapCatMessageParser.ParseMessage(node, selfId, _ => false).Text);
                var sender = GetNodeSender(node);
                var label = sender.Length == 0 ? text : $"{sender}：{text}";

                if (nodeCount > 0) builder.Append('；');
                builder.Append(label);
                nodeCount++;

                // 只看前几个节点，避免长转发把消息正文撑爆
                if (nodeCount >= MaxNodes) break;
                if (builder.Length >= MaxSummaryLength) break;
            }

            if (nodeCount == 0) return null;

            var body = builder.ToString();
            if (body.Length > MaxSummaryLength) body = body[..MaxSummaryLength] + "…";

            _logger.LogDebug("已反查合并转发 {ForwardId}，共 {Count} 个节点", forwardId, messages.GetArrayLength());
            return $"[合并转发({messages.GetArrayLength()}条)：{body}]";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "反查合并转发 {ForwardId} 失败", forwardId);
            return null;
        }
    }

    /// <summary>取节点发送者展示名：优先群名片、昵称，其次节点自带的昵称。</summary>
    private static string GetNodeSender(JsonElement node)
    {
        var target = Unwrap(node);
        if (target.TryGetProperty("sender", out var sender) && sender.ValueKind == JsonValueKind.Object)
        {
            var card = NapCatMessageParser.GetString(sender, "card");
            if (!string.IsNullOrWhiteSpace(card)) return card;

            var nickname = NapCatMessageParser.GetString(sender, "nickname");
            if (!string.IsNullOrWhiteSpace(nickname)) return nickname;
        }

        return NapCatMessageParser.GetString(target, "nickname")
               ?? NapCatMessageParser.GetString(target, "name")
               ?? string.Empty;
    }

    /// <summary>兼容 type=node 的节点结构，把真正的消息体从 data 里取出来。</summary>
    private static JsonElement Unwrap(JsonElement node)
    {
        if (NapCatMessageParser.GetString(node, "type") == "node"
            && node.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object)
        {
            return data;
        }

        return node;
    }

    /// <summary>把节点正文压成一行短摘要，空消息给出可读占位。</summary>
    private static string Summarize(string text)
    {
        var single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (single.Length == 0) return "[非文本消息]";

        return single.Length <= MaxNodeTextLength ? single : single[..MaxNodeTextLength] + "…";
    }
}
