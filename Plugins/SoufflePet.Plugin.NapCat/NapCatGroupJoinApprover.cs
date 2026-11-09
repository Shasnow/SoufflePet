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

/// <summary>入群自动审批：按配置的群范围与关键词同意或拒绝加群申请。</summary>
internal sealed class NapCatGroupJoinApprover
{
    /// <summary>日志里展示的申请验证信息最大字数。</summary>
    private const int MaxCommentLogLength = 80;

    private readonly ILogger _logger;
    private readonly NapCatConnection _connection;
    private readonly NapCatConfig _config;

    public NapCatGroupJoinApprover(ILogger logger, NapCatConnection connection, NapCatConfig config)
    {
        _logger = logger;
        _connection = connection;
        _config = config;
    }

    /// <summary>该事件是否为需要自动审批的加群申请。</summary>
    public bool ShouldHandle(JsonElement root)
        => NapCatMessageParser.GetString(root, "post_type") == "request"
           && NapCatMessageParser.GetString(root, "request_type") == "group"
           && NapCatMessageParser.GetString(root, "sub_type") == "add";

    /// <summary>处理一条加群申请：命中拒绝词则拒绝，命中同意词或未配置同意词则同意，否则留给人工。</summary>
    public async Task HandleAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!_config.AutoApproveJoin) return;

        var groupId = NapCatMessageParser.GetInt64(root, "group_id");
        var userId = NapCatMessageParser.GetInt64(root, "user_id");
        var flag = NapCatMessageParser.GetString(root, "flag");
        var comment = NapCatMessageParser.GetString(root, "comment") ?? string.Empty;

        if (groupId <= 0 || userId <= 0 || string.IsNullOrWhiteSpace(flag)) return;
        if (!_config.ShouldAutoApprove(groupId)) return;

        var decision = Evaluate(comment);
        if (decision == JoinDecision.LeaveToManual)
        {
            _logger.LogInformation(
                "加群申请未自动处理：群 {GroupId}，用户 {UserId}，验证信息「{Comment}」",
                groupId,
                userId,
                Truncate(comment));
            return;
        }

        var approve = decision == JoinDecision.Approve;
        var parameters = new Dictionary<string, object?>
        {
            ["flag"] = flag,
            ["sub_type"] = "add",
            ["approve"] = approve,
        };
        if (!approve) parameters["reason"] = _config.RejectReason;

        var socket = _connection.Socket;
        if (socket is not { State: WebSocketState.Open })
        {
            _logger.LogWarning("NapCat WebSocket 未连接，加群申请没有自动处理");
            return;
        }

        await _connection.SendActionAsync(socket, "set_group_add_request", parameters, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "已{Action}加群申请：群 {GroupId}，用户 {UserId}，验证信息「{Comment}」",
            approve ? "同意" : "拒绝",
            groupId,
            userId,
            Truncate(comment));
    }

    /// <summary>按关键词判定：拒绝词优先，其次同意词；未配置同意词时直接同意。</summary>
    private JoinDecision Evaluate(string comment)
    {
        if (ContainsAny(comment, _config.RejectKeywords)) return JoinDecision.Reject;
        if (_config.ApproveKeywords.Length == 0) return JoinDecision.Approve;

        return ContainsAny(comment, _config.ApproveKeywords) ? JoinDecision.Approve : JoinDecision.LeaveToManual;
    }

    private static bool ContainsAny(string text, string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>把验证信息压成单行并截断，避免异常长的申请内容刷满日志。</summary>
    private static string Truncate(string comment)
    {
        var single = comment.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (single.Length == 0) return "（无）";

        return single.Length <= MaxCommentLogLength ? single : single[..MaxCommentLogLength] + "…";
    }

    private enum JoinDecision
    {
        /// <summary>自动同意。</summary>
        Approve,

        /// <summary>自动拒绝。</summary>
        Reject,

        /// <summary>不处理，留给管理员手动审批。</summary>
        LeaveToManual,
    }
}