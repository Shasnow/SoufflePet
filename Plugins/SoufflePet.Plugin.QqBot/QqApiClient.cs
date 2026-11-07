// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SoufflePet.Plugin.Sdk;

namespace SoufflePet.Plugins.QqBot;

/// <summary>QQ 官方机器人会话类型：群聊或单聊（C2C）。</summary>
internal enum QqConversationKind
{
    Group,
    Private,
}

/// <summary>会话目标：群聊用 group_openid，单聊用 user_openid。</summary>
internal readonly record struct QqConversationTarget(QqConversationKind Kind, string OpenId);

/// <summary>一条被动回复：消息 id 与同一消息下递增的序号。</summary>
internal sealed record QqPassiveReply(string MessageId, int Sequence);

/// <summary>JSON 读取辅助，兼容 QQ 接口里字符串/数字混用的情况。</summary>
internal static class QqJson
{
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

    public static string? GetString(JsonElement element, string outer, string inner)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(outer, out var nested))
            return null;

        return GetString(nested, inner);
    }

    public static int? GetInt(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    public static int? GetInt(JsonElement element, string outer, string inner)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(outer, out var nested))
            return null;

        return GetInt(nested, inner);
    }
}

/// <summary>
/// QQ 官方机器人 API 客户端：负责 OAuth access_token 的缓存与刷新，以及文本/富媒体消息的发送与文件上传。
/// </summary>
internal sealed class QqApiClient
{
    /// <summary>token 提前刷新的秒数，避免请求边界上刚好过期。</summary>
    private const int TokenRefreshAheadSeconds = 60;

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly string _appId;
    private readonly string _clientSecret;
    private readonly string _apiBaseUrl;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    private string? _accessToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    public QqApiClient(string appId, string clientSecret, string apiBaseUrl, ILogger logger)
    {
        _appId = appId;
        _clientSecret = clientSecret;
        _apiBaseUrl = apiBaseUrl.TrimEnd('/');
        _logger = logger;
        _http = Ipv4HttpClient.Create();
    }

    /// <summary>返回可用的 access_token：缓存有效时直接复用，否则串行刷新。</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (TryGetCachedToken(out var cached)) return cached;

        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCachedToken(out cached)) return cached;
            return await FetchAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private bool TryGetCachedToken(out string token)
    {
        token = _accessToken ?? string.Empty;
        return token.Length > 0 && DateTimeOffset.UtcNow < _tokenExpiresAt;
    }

    private async Task<string> FetchAccessTokenAsync(CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["appId"] = _appId,
            ["clientSecret"] = _clientSecret,
        };

        var url = _apiBaseUrl + "/app/getAppAccessToken";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"QQ 获取 access_token 失败（HTTP {(int)response.StatusCode}）：{Truncate(body)}");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"QQ 获取 access_token 返回非 JSON：{Truncate(body)}");
        }

        using (document)
        {
            var root = document.RootElement;
            var code = QqJson.GetInt(root, "code");
            if (code is not null && code != 0)
            {
                var message = QqJson.GetString(root, "message")
                              ?? QqJson.GetString(root, "msg")
                              ?? Truncate(body);
                throw new InvalidOperationException($"QQ 获取 access_token 失败（code={code}）：{message}");
            }

            var token = QqJson.GetString(root, "access_token");
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException($"QQ 获取 access_token 响应缺少 access_token：{Truncate(body)}");

            var expires = 7200;
            if (QqJson.GetInt(root, "expires_in") is { } number)
            {
                expires = number;
            }
            else if (int.TryParse(QqJson.GetString(root, "expires_in"), out var parsed))
            {
                expires = parsed;
            }

            _accessToken = token;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(
                Math.Max(TokenRefreshAheadSeconds, expires - TokenRefreshAheadSeconds));

            _logger.LogInformation("QQ 机器人 access_token 已刷新，有效期 {Seconds} 秒", expires);
            return token;
        }
    }

    /// <summary>发送一条文本消息；passive 为 null 时发送主动消息。</summary>
    public async Task<string?> SendTextAsync(
        QqConversationTarget target,
        string content,
        QqPassiveReply? passive,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["content"] = content,
            ["msg_type"] = 0,
        };
        AddPassive(payload, passive);

        var root = await PostForJsonAsync(
                BuildMessagesPath(target), payload, "发送消息", cancellationToken)
            .ConfigureAwait(false);
        return QqJson.GetString(root, "id");
    }

    /// <summary>发送已上传的富媒体消息（msg_type=7）。</summary>
    public async Task SendMediaAsync(
        QqConversationTarget target,
        string fileInfo,
        QqPassiveReply? passive,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["msg_type"] = 7,
            ["media"] = new Dictionary<string, object?> { ["file_info"] = fileInfo },
        };
        AddPassive(payload, passive);

        await PostForJsonAsync(BuildMessagesPath(target), payload, "发送富媒体消息", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>上传本地图片或文件，返回可用于 media 的 file_info。</summary>
    public async Task<string> UploadAsync(
        QqConversationTarget target,
        string filePath,
        int fileType,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        var payload = new Dictionary<string, object?>
        {
            ["file_type"] = fileType,
            ["file_data"] = Convert.ToBase64String(bytes),
            ["file_name"] = Path.GetFileName(filePath),
            ["srv_send_msg"] = false,
        };

        var root = await PostForJsonAsync(
                BuildFilesPath(target), payload, "上传文件", cancellationToken)
            .ConfigureAwait(false);

        var fileInfo = QqJson.GetString(root, "file_info");
        if (string.IsNullOrWhiteSpace(fileInfo))
            throw new InvalidOperationException($"QQ 上传文件响应缺少 file_info：{Truncate(root.GetRawText())}");

        return fileInfo;
    }

    private static void AddPassive(Dictionary<string, object?> payload, QqPassiveReply? passive)
    {
        if (passive is null) return;
        payload["msg_id"] = passive.MessageId;
        payload["msg_seq"] = passive.Sequence;
    }

    private static string BuildMessagesPath(QqConversationTarget target)
        => target.Kind == QqConversationKind.Group
            ? $"/v2/groups/{Uri.EscapeDataString(target.OpenId)}/messages"
            : $"/v2/users/{Uri.EscapeDataString(target.OpenId)}/messages";

    private static string BuildFilesPath(QqConversationTarget target)
        => target.Kind == QqConversationKind.Group
            ? $"/v2/groups/{Uri.EscapeDataString(target.OpenId)}/files"
            : $"/v2/users/{Uri.EscapeDataString(target.OpenId)}/files";

    private async Task<JsonElement> PostForJsonAsync(
        string path,
        Dictionary<string, object?> payload,
        string action,
        CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, _apiBaseUrl + path)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"QQBot {token}");

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"QQ {action}失败（HTTP {(int)response.StatusCode}）：{Truncate(body)}");

        JsonDocument? document = null;
        try
        {
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                // 下面统一按非 JSON 处理
            }

            if (document is null)
                throw new InvalidOperationException($"QQ {action}返回非 JSON：{Truncate(body)}");

            var root = document.RootElement;
            var code = QqJson.GetInt(root, "code") ?? QqJson.GetInt(root, "err_code");
            if (code is not null && code != 0)
            {
                var message = QqJson.GetString(root, "message")
                              ?? QqJson.GetString(root, "msg")
                              ?? Truncate(body);
                throw new InvalidOperationException($"QQ {action}失败（code={code}）：{message}");
            }

            return root.Clone();
        }
        finally
        {
            document?.Dispose();
        }
    }

    private static string Truncate(string text)
        => text.Length <= 300 ? text : text[..300] + "…";
}
