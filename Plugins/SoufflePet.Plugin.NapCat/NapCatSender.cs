// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SoufflePet.Plugin.Sdk;

namespace SoufflePet.Plugins.NapCat;

/// <summary>通过 OneBot action 向会话发送文本、图片与文件，并记录自己发出的消息 id。</summary>
internal sealed class NapCatSender
{
    /// <summary>图片作为消息段发送时的最大字节数（base64 编码会膨胀）。</summary>
    private const long MaxSendImageBytes = 5L * 1024 * 1024;

    /// <summary>文件走 base64 上传的上限；更大的文件传本地绝对路径，避免内存与 WebSocket 压力。</summary>
    private const long MaxBase64FileBytes = 10L * 1024 * 1024;

    private readonly ILogger _logger;
    private readonly NapCatConnection _connection;
    private readonly NapCatQuoteTracker _quotes;

    public NapCatSender(ILogger logger, NapCatConnection connection, NapCatQuoteTracker quotes)
    {
        _logger = logger;
        _connection = connection;
        _quotes = quotes;
    }

    /// <summary>向指定会话发送文本，按配置的字数上限拆成多条。</summary>
    public async Task SendMessageAsync(
        (string Kind, long Id) conversation,
        string message,
        int maxReplyChars)
    {
        var socket = _connection.Socket;
        if (socket is not { State: WebSocketState.Open })
            throw new InvalidOperationException("NapCat WebSocket 未连接，消息没有发送。");

        var isGroup = conversation.Kind == "group";
        foreach (var chunk in SplitMessage(message, maxReplyChars))
        {
            var parameters = isGroup
                ? new Dictionary<string, object?>
                {
                    ["group_id"] = conversation.Id,
                    ["message"] = chunk,
                    ["auto_escape"] = true,
                }
                : new Dictionary<string, object?>
                {
                    ["user_id"] = conversation.Id,
                    ["message"] = chunk,
                    ["auto_escape"] = true,
                };

            var response = await _connection.SendActionAsync(
                    socket,
                    isGroup ? "send_group_msg" : "send_private_msg",
                    parameters)
                .ConfigureAwait(false);

            // 记住自己发出的消息 id，供之后快速识别「他人引用桌宠消息」
            var sentId = GetSentMessageId(response);
            if (sentId is null)
                _logger.LogDebug("NapCat 发送响应缺少 message_id：{Raw}", response.GetRawText());
            _quotes.RememberSentMessageId(sentId);
        }

        _logger.LogInformation("已向{Kind} {Id} 发送消息", isGroup ? "群" : "用户", conversation.Id);
    }

    /// <summary>
    /// 发送附件：图片走消息段（base64），文件走 upload_group_file / upload_private_file；
    /// 小文件用 base64 避免路径权限差异，大文件保留绝对路径减少内存占用。
    /// </summary>
    public async Task SendAttachmentAsync(
        (string Kind, long Id) conversation,
        PlatformAttachment attachment)
    {
        var path = Path.GetFullPath(attachment.FilePath);
        if (!File.Exists(path))
            throw new InvalidOperationException($"文件不存在：{path}");

        var name = string.IsNullOrWhiteSpace(attachment.DisplayName)
            ? Path.GetFileName(path)
            : attachment.DisplayName.Trim();

        var socket = _connection.Socket;
        if (socket is not { State: WebSocketState.Open })
            throw new InvalidOperationException("NapCat WebSocket 未连接，附件没有发送。");

        var isGroup = conversation.Kind == "group";
        if (attachment.Kind == PlatformAttachmentKind.Image)
        {
            await SendImageAsync(socket, conversation, path, name).ConfigureAwait(false);
        }
        else
        {
            await SendFileAsync(socket, conversation, path, name).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "已向{Kind} {Id} 发送{Label}「{Name}」",
            isGroup ? "群" : "用户",
            conversation.Id,
            attachment.Kind == PlatformAttachmentKind.Image ? "图片" : "文件",
            name);
    }

    /// <summary>图片按 OneBot 消息段发送：数组格式避免 CQ 码转义，base64 不依赖 NapCat 的文件权限。</summary>
    private async Task SendImageAsync(
        ClientWebSocket socket,
        (string Kind, long Id) conversation,
        string path,
        string name)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxSendImageBytes)
            throw new InvalidOperationException(
                $"图片「{name}」大小为 {info.Length} 字节，超过 {MaxSendImageBytes} 的发送上限。");

        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        var parameters = BuildMessageParameters(
            conversation,
            new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "image",
                    ["data"] = new Dictionary<string, object?>
                    {
                        ["file"] = "base64://" + Convert.ToBase64String(bytes),
                    },
                },
            });

        var response = await _connection.SendActionAsync(
                socket,
                conversation.Kind == "group" ? "send_group_msg" : "send_private_msg",
                parameters)
            .ConfigureAwait(false);
        _quotes.RememberSentMessageId(GetSentMessageId(response));
    }

    /// <summary>文件走平台上传接口；小文件用 base64，大文件传绝对路径。</summary>
    private async Task SendFileAsync(
        ClientWebSocket socket,
        (string Kind, long Id) conversation,
        string path,
        string name)
    {
        var info = new FileInfo(path);
        var file = info.Length <= MaxBase64FileBytes
            ? "base64://" + Convert.ToBase64String(await File.ReadAllBytesAsync(path).ConfigureAwait(false))
            : path;

        var isGroup = conversation.Kind == "group";
        var parameters = new Dictionary<string, object?>
        {
            [isGroup ? "group_id" : "user_id"] = conversation.Id,
            ["file"] = file,
            ["name"] = name,
        };

        var response = await _connection.SendActionAsync(
                socket,
                isGroup ? "upload_group_file" : "upload_private_file",
                parameters)
            .ConfigureAwait(false);
        _quotes.RememberSentMessageId(GetSentMessageId(response));
    }

    /// <summary>构造附件消息参数：附件用消息段数组，不设置 auto_escape。</summary>
    private static Dictionary<string, object?> BuildMessageParameters(
        (string Kind, long Id) conversation,
        object message) => new()
    {
        [conversation.Kind == "group" ? "group_id" : "user_id"] = conversation.Id,
        ["message"] = message,
    };

    /// <summary>从发送响应里取 message_id；没有时返回 null。</summary>
    private static string? GetSentMessageId(JsonElement response) =>
        response.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? NapCatMessageParser.GetString(data, "message_id")
            : null;

    /// <summary>按字数拆分超长回复，避免拆开代理对（emoji）。</summary>
    private static IEnumerable<string> SplitMessage(string text, int maxChars)
    {
        text = text.Trim();
        var index = 0;
        while (index < text.Length)
        {
            var length = Math.Min(maxChars, text.Length - index);
            if (index + length < text.Length && char.IsHighSurrogate(text[index + length - 1])) length--;
            if (length <= 0) break;

            yield return text.Substring(index, length);
            index += length;
        }
    }
}
