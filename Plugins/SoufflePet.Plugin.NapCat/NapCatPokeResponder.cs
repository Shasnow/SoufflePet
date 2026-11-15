// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SoufflePet.Plugins.NapCat;

/// <summary>
/// 戳一戳响应：捕获「戳的是桌宠」的通知事件，按配置转发给桌宠、按概率回戳，
/// 并对同一用户做冷却——冷却期内再次戳桌宠直接忽略，计时从上一次有效响应开始。
/// 别人戳其他用户时，还可以按跟戳概率跟着戳被戳的人。
/// </summary>
internal sealed class NapCatPokeResponder
{
    /// <summary>冷却表最多记录的用人数，超出时清理已过期条目。</summary>
    private const int MaxTrackedUsers = 512;

    private readonly ILogger _logger;
    private readonly NapCatConnection _connection;
    private readonly NapCatConfig _config;

    /// <summary>把戳一戳转成唤醒内容：参数为会话 id、文本行、发送者昵称与群号。</summary>
    private readonly Action<string, string, string, long> _onResponse;

    private readonly Lock _sync = new();
    private readonly Dictionary<long, DateTimeOffset> _lastResponses = new();

    public NapCatPokeResponder(
        ILogger logger,
        NapCatConnection connection,
        NapCatConfig config,
        Action<string, string, string, long> onResponse)
    {
        _logger = logger;
        _connection = connection;
        _config = config;
        _onResponse = onResponse;
    }

    /// <summary>处理通知事件；不是戳一戳、或命中冷却时直接返回。</summary>
    public void Handle(JsonElement root, long selfId)
    {
        if (!IsPokeEvent(root)) return;

        var eventSelfId = NapCatMessageParser.GetInt64(root, "self_id");
        if (eventSelfId > 0) selfId = eventSelfId;
        if (selfId <= 0) return;

        // 兼容两种事件写法：user_id（OneBot 标准）与 sender_id（部分实现）
        var userId = NapCatMessageParser.GetInt64(root, "user_id");
        if (userId <= 0) userId = NapCatMessageParser.GetInt64(root, "sender_id");
        if (userId <= 0 || userId == selfId) return;

        var groupId = NapCatMessageParser.GetInt64(root, "group_id");
        if (!IsAllowed(userId, groupId)) return;

        var targetId = NapCatMessageParser.GetInt64(root, "target_id");

        // 戳的是其他用户：按跟戳概率跟着戳被戳的人，不参与下面的响应与冷却
        if (targetId > 0 && targetId != selfId)
        {
            TryFollowPoke(userId, targetId, groupId);
            return;
        }

        if (IsCoolingDown(userId)) return;

        // 智能回复由开关控制；回戳只看概率，两者互不依赖
        var wantLlm = _config.PokeLlmReply;
        var wantPoke = RollPokeChance();
        if (!wantLlm && !wantPoke) return;

        MarkResponded(userId);

        var sender = NapCatMessageParser.GetSenderName(root, userId);
        var conversationId = groupId > 0
            ? $"group:{groupId.ToString(CultureInfo.InvariantCulture)}"
            : $"private:{userId.ToString(CultureInfo.InvariantCulture)}";

        if (wantLlm) _onResponse(conversationId, $"[{conversationId}] {sender} 戳了戳你", sender, groupId);

        if (wantPoke)
        {
            _logger.LogInformation("收到 {UserId} 的戳一戳，按配置回戳", userId);
            _ = PokeBackAsync(groupId, userId);
        }
    }

    /// <summary>跟戳：别人戳的是其他用户时，按配置概率跟着戳同一个人；仅群聊生效，避免私聊骚扰。</summary>
    private void TryFollowPoke(long userId, long targetId, long groupId)
    {
        if (groupId <= 0 || targetId <= 0) return;
        if (_config.FollowPokeProbability <= 0) return;
        if (_config.FollowPokeProbability < 1
            && Random.Shared.NextDouble() >= _config.FollowPokeProbability)
        {
            return;
        }

        _logger.LogInformation(
            "跟戳：{UserId} 戳了群 {GroupId} 的 {TargetId}，桌宠跟着戳了一下",
            userId,
            groupId,
            targetId);
        _ = PokeBackAsync(groupId, targetId);
    }

    /// <summary>是否为戳一戳通知；NapCat 可能上报为 notify/poke，也可能直接是 poke。</summary>
    private static bool IsPokeEvent(JsonElement root)
    {
        var noticeType = NapCatMessageParser.GetString(root, "notice_type");
        var subType = NapCatMessageParser.GetString(root, "sub_type");
        return noticeType == "poke"
               || (noticeType == "notify" && string.Equals(subType, "poke", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>白名单与消息链路保持一致：群聊看群号白名单，私聊看 QQ 号白名单；留空表示全部放行。</summary>
    private bool IsAllowed(long userId, long groupId)
    {
        if (groupId > 0) return _config.AllowedGroups.Count == 0 || _config.AllowedGroups.Contains(groupId);
        return _config.AllowedUsers.Count == 0 || _config.AllowedUsers.Contains(userId);
    }

    /// <summary>冷却期内忽略同一用户的戳一戳；0 表示不冷却。</summary>
    private bool IsCoolingDown(long userId)
    {
        if (_config.PokeCooldownSeconds <= 0) return false;

        var cooldown = TimeSpan.FromSeconds(_config.PokeCooldownSeconds);
        lock (_sync)
        {
            return _lastResponses.TryGetValue(userId, out var last)
                   && DateTimeOffset.UtcNow - last < cooldown;
        }
    }

    /// <summary>记录有效响应的时间点，冷却从这里开始计算；顺带清理过期条目。</summary>
    private void MarkResponded(long userId)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            _lastResponses[userId] = now;
            if (_lastResponses.Count < MaxTrackedUsers) return;

            var expiry = now - TimeSpan.FromSeconds(Math.Max(_config.PokeCooldownSeconds, 60));
            foreach (var stale in _lastResponses.Where(pair => pair.Value < expiry).Select(pair => pair.Key).ToList())
                _lastResponses.Remove(stale);
        }
    }

    /// <summary>回戳概率（0-1）：0 表示从不回戳，1 表示每次都回戳。</summary>
    private bool RollPokeChance()
        => _config.PokeBackProbability > 0
           && (_config.PokeBackProbability >= 1 || Random.Shared.NextDouble() < _config.PokeBackProbability);

    /// <summary>通过 send_poke 回戳；群聊带上群号，失败只记日志，不影响消息链路。</summary>
    private async Task PokeBackAsync(long groupId, long userId)
    {
        try
        {
            var socket = _connection.Socket;
            if (socket is not { State: WebSocketState.Open })
            {
                _logger.LogDebug("NapCat WebSocket 未连接，跳过回戳 {UserId}", userId);
                return;
            }

            var parameters = new Dictionary<string, object?> { ["user_id"] = userId };
            if (groupId > 0) parameters["group_id"] = groupId;

            await _connection.SendActionAsync(socket, "send_poke", parameters).ConfigureAwait(false);
            _logger.LogInformation(
                "已戳{Scope} {UserId}",
                groupId > 0 ? $"群 {groupId} 的" : string.Empty,
                userId);
        }
        catch (OperationCanceledException)
        {
            // 插件正在卸载，忽略
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "回戳用户 {UserId} 失败", userId);
        }
    }
}