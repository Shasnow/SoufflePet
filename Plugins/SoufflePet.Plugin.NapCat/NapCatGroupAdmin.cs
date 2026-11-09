// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SoufflePet.Plugins.NapCat;

/// <summary>把群管理工具调用翻译成 OneBot action，并统一做可管理群校验。</summary>
internal sealed class NapCatGroupAdmin
{
    /// <summary>单次禁言时长上限：30 天。</summary>
    private const int MaxBanSeconds = 30 * 24 * 60 * 60;

    /// <summary>成员列表单次最多返回的条数；单个 QQ 群最多 2000 名成员。</summary>
    private const int MaxMemberListCount = 2000;

    private readonly ILogger _logger;
    private readonly NapCatConnection _connection;
    private readonly NapCatConfig _config;

    public NapCatGroupAdmin(ILogger logger, NapCatConnection connection, NapCatConfig config)
    {
        _logger = logger;
        _connection = connection;
        _config = config;
    }

    /// <summary>禁言成员；<paramref name="durationSeconds" /> 填 0 或负数表示解除禁言。</summary>
    public async Task<string> BanMemberAsync(
        long groupId,
        long userId,
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        EnsureGroup(groupId);
        EnsureUser(userId);

        var seconds = durationSeconds <= 0 ? 0 : Math.Clamp(durationSeconds, 1, MaxBanSeconds);
        await SendActionAsync(
                "set_group_ban",
                new Dictionary<string, object?>
                {
                    ["group_id"] = groupId,
                    ["user_id"] = userId,
                    ["duration"] = seconds,
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (seconds == 0)
        {
            _logger.LogInformation("已解除群 {GroupId} 中成员 {UserId} 的禁言", groupId, userId);
            return $"已解除群 {groupId} 中成员 {userId} 的禁言。";
        }

        _logger.LogInformation("已在群 {GroupId} 禁言 {UserId} {Seconds} 秒", groupId, userId, seconds);
        return $"已在群 {groupId} 禁言成员 {userId}，时长 {FormatDuration(seconds)}。";
    }

    /// <summary>开启或关闭全员禁言。</summary>
    public async Task<string> SetWholeBanAsync(long groupId, bool enable, CancellationToken cancellationToken)
    {
        EnsureGroup(groupId);

        await SendActionAsync(
                "set_group_whole_ban",
                new Dictionary<string, object?>
                {
                    ["group_id"] = groupId,
                    ["enable"] = enable,
                },
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("已{Action}群 {GroupId} 的全员禁言", enable ? "开启" : "关闭", groupId);
        return enable ? $"已开启群 {groupId} 的全员禁言。" : $"已关闭群 {groupId} 的全员禁言。";
    }

    /// <summary>把成员移出群；<paramref name="rejectAddRequest" /> 表示同时拒绝其再次加群。</summary>
    public async Task<string> KickMemberAsync(
        long groupId,
        long userId,
        bool rejectAddRequest,
        CancellationToken cancellationToken)
    {
        EnsureGroup(groupId);
        EnsureUser(userId);

        await SendActionAsync(
                "set_group_kick",
                new Dictionary<string, object?>
                {
                    ["group_id"] = groupId,
                    ["user_id"] = userId,
                    ["reject_add_request"] = rejectAddRequest,
                },
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "已把 {UserId} 移出群 {GroupId}，拒绝再次加群：{Reject}",
            userId,
            groupId,
            rejectAddRequest);
        return rejectAddRequest
            ? $"已将成员 {userId} 移出群 {groupId}，并拒绝其再次加群。"
            : $"已将成员 {userId} 移出群 {groupId}。";
    }

    /// <summary>设置成员群名片；<paramref name="card" /> 为空表示清空名片。</summary>
    public async Task<string> SetCardAsync(
        long groupId,
        long userId,
        string card,
        CancellationToken cancellationToken)
    {
        EnsureGroup(groupId);
        EnsureUser(userId);

        var value = card?.Trim() ?? string.Empty;
        await SendActionAsync(
                "set_group_card",
                new Dictionary<string, object?>
                {
                    ["group_id"] = groupId,
                    ["user_id"] = userId,
                    ["card"] = value,
                },
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("已修改群 {GroupId} 中成员 {UserId} 的群名片", groupId, userId);
        return value.Length == 0
            ? $"已清空群 {groupId} 中成员 {userId} 的群名片。"
            : $"已把群 {groupId} 中成员 {userId} 的群名片改为「{value}」。";
    }

    /// <summary>读取群成员列表，供模型确认要操作的对象；成员较多时按 offset 翻页。</summary>
    public async Task<string> GetMemberListAsync(
        long groupId,
        int count,
        int offset,
        CancellationToken cancellationToken)
    {
        EnsureGroup(groupId);

        var response = await SendActionAsync(
                "get_group_member_list",
                new Dictionary<string, object?> { ["group_id"] = groupId },
                cancellationToken)
            .ConfigureAwait(false);

        if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return $"没有读到群 {groupId} 的成员列表。";

        var total = data.GetArrayLength();
        if (total == 0) return $"群 {groupId} 没有返回成员信息。";

        var skip = Math.Max(offset, 0);
        if (skip >= total) return $"群 {groupId} 共 {total} 人，起始位置 {skip} 超出范围。";

        var limit = Math.Clamp(count <= 0 ? 50 : count, 1, MaxMemberListCount);
        var builder = new StringBuilder();
        var index = 0;
        var shown = 0;
        foreach (var member in data.EnumerateArray())
        {
            index++;
            if (index <= skip) continue;
            if (shown >= limit) break;
            if (member.ValueKind != JsonValueKind.Object) continue;

            shown++;
            if (builder.Length > 0) builder.Append('\n');

            var userId = NapCatMessageParser.GetInt64(member, "user_id");
            var nickname = NapCatMessageParser.GetString(member, "nickname") ?? string.Empty;
            var card = NapCatMessageParser.GetString(member, "card") ?? string.Empty;
            var role = FormatRole(NapCatMessageParser.GetString(member, "role"));

            builder.Append($"{userId.ToString(CultureInfo.InvariantCulture)} {nickname}");
            if (card.Length > 0) builder.Append($"（群名片：{card}）");
            if (role.Length > 0) builder.Append($" [{role}]");
        }

        if (shown == 0) return $"群 {groupId} 共 {total} 人，没有更多成员。";

        var end = skip + shown;
        var more = end < total ? $"，还有 {total - end} 人可用 offset={end} 继续读取" : string.Empty;
        return $"群 {groupId} 共 {total} 人，显示第 {skip + 1}-{end} 人{more}：\n{builder}";
    }

    /// <summary>调用 OneBot action；连接未就绪时抛出可读异常，由宿主转成工具错误文本。</summary>
    private async Task<JsonElement> SendActionAsync(
        string action,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        var socket = _connection.Socket;
        if (socket is not { State: WebSocketState.Open })
            throw new InvalidOperationException("NapCat WebSocket 未连接，群管理操作没有执行。");

        return await _connection.SendActionAsync(socket, action, parameters, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>校验群号在允许管理的范围内；留空表示不限制。</summary>
    private void EnsureGroup(long groupId)
    {
        if (groupId <= 0)
            throw new ArgumentException("群号必须大于 0。", nameof(groupId));

        if (!_config.CanManage(groupId))
            throw new InvalidOperationException($"群 {groupId} 不在允许管理的群列表中，操作已拒绝。");
    }

    private static void EnsureUser(long userId)
    {
        if (userId <= 0) throw new ArgumentException("QQ 号必须大于 0。", nameof(userId));
    }

    /// <summary>把时长秒数转成便于阅读的中文描述。</summary>
    private static string FormatDuration(int seconds)
    {
        if (seconds % (24 * 60 * 60) == 0) return $"{seconds / (24 * 60 * 60)} 天";
        if (seconds % (60 * 60) == 0) return $"{seconds / (60 * 60)} 小时";
        if (seconds % 60 == 0) return $"{seconds / 60} 分钟";
        return $"{seconds} 秒";
    }

    private static string FormatRole(string? role) => role switch
    {
        "owner" => "群主",
        "admin" => "管理员",
        "member" => "成员",
        _ => string.Empty,
    };
}