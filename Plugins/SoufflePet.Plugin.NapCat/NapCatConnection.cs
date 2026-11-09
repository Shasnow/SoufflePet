// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SoufflePet.Plugins.NapCat;

/// <summary>NapCat 正向 WebSocket 连接：维持重连、接收事件、发送 OneBot action 并等待响应。</summary>
internal sealed class NapCatConnection
{
    /// <summary>发送 action 后等待 NapCat 响应的超时。</summary>
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(10);

    /// <summary>断线后的重连间隔。</summary>
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    /// <summary>单条 WebSocket 消息的长度上限，防止异常数据无限占用内存。</summary>
    private const int MaxPayloadBytes = 8 * 1024 * 1024;

    private readonly ILogger _logger;
    private readonly string _accessToken;
    private readonly Action<JsonElement> _onEvent;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Lock _sync = new();
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();

    private ClientWebSocket? _socket;
    private Task? _pump;

    public NapCatConnection(ILogger logger, string accessToken, Action<JsonElement> onEvent)
    {
        _logger = logger;
        _accessToken = accessToken;
        _onEvent = onEvent;
    }

    /// <summary>连接关闭时取消后台下载、action 等待等操作。</summary>
    public CancellationToken ShutdownToken => _cts.Token;

    /// <summary>当前 WebSocket；未连接或已断开时为 null。</summary>
    public ClientWebSocket? Socket
    {
        get
        {
            lock (_sync) return _socket;
        }
    }

    /// <summary>启动连接循环（连上后持续接收事件，断开或异常时等待一段时间再重连）。</summary>
    public void Start(Uri uri)
        => _pump = Task.Run(() => RunAsync(uri, _cts.Token), CancellationToken.None);

    /// <summary>停止连接、取消所有等待中的 action，并释放连接资源。</summary>
    public async Task ShutdownAsync()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放过，忽略
        }

        var socket = Socket;
        if (socket is { State: WebSocketState.Open })
        {
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "shutdown", closeTimeout.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "关闭 NapCat WebSocket 时出错");
            }
        }

        if (_pump is { } pump)
        {
            try
            {
                await pump.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "等待 NapCat 连接循环退出时超时");
            }
        }

        List<TaskCompletionSource<JsonElement>> pending;
        lock (_sync)
        {
            pending = [.. _pending.Values];
            _pending.Clear();
        }

        foreach (var completion in pending) completion.TrySetCanceled();

        _cts.Dispose();
        try
        {
            _sendGate.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // 已有发送操作释放过，忽略
        }
    }

    /// <summary>连接循环：连上后持续接收事件，断开或异常时等待一段时间再重连。</summary>
    private async Task RunAsync(Uri uri, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ClientWebSocket? socket = null;
            try
            {
                socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                if (!string.IsNullOrWhiteSpace(_accessToken))
                    socket.Options.SetRequestHeader("Authorization", $"Bearer {_accessToken}");

                await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
                lock (_sync) _socket = socket;
                _logger.LogInformation("NapCat WebSocket 已连接：{Url}", uri);

                await PumpAsync(socket, cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("NapCat WebSocket 已断开");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NapCat WebSocket 连接异常");
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_socket, socket)) _socket = null;
                }

                socket?.Dispose();
            }

            try
            {
                await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>读取 WebSocket 帧：把分片拼成完整 JSON，再分派给事件或 action 响应。</summary>
    private async Task PumpAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var payload = new MemoryStream();

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                _logger.LogInformation(
                    "NapCat 关闭了连接：{Status} {Reason}",
                    result.CloseStatus,
                    result.CloseStatusDescription);
                return;
            }

            payload.Write(buffer, 0, result.Count);
            if (payload.Length > MaxPayloadBytes)
            {
                _logger.LogWarning("NapCat 推送的单条数据超过 {Size} 字节，已丢弃", MaxPayloadBytes);
                payload.SetLength(0);
                continue;
            }

            if (!result.EndOfMessage) continue;

            var json = Encoding.UTF8.GetString(payload.GetBuffer(), 0, (int)payload.Length);
            payload.SetLength(0);
            HandlePayload(json);
        }
    }

    private void HandlePayload(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            // 先看 echo：这是本插件发出的 action 的响应
            if (root.TryGetProperty("echo", out var echo) && echo.ValueKind == JsonValueKind.String)
            {
                CompletePending(echo.GetString()!, root);
                return;
            }

            // 事件（消息、加群申请等）交给插件同步分派；需要等待 action 响应的处理由插件放到后台任务
            _onEvent(root);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "解析 NapCat 推送内容失败");
        }
    }

    private void CompletePending(string echo, JsonElement response)
    {
        TaskCompletionSource<JsonElement>? completion;
        lock (_sync)
        {
            if (!_pending.TryGetValue(echo, out completion)) return;
        }

        completion.TrySetResult(response.Clone());
    }

    /// <summary>发送一条 OneBot action，并等待带相同 echo 的响应；返回整个响应（含 data）。</summary>
    public async Task<JsonElement> SendActionAsync(
        ClientWebSocket socket,
        string action,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var echo = Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["action"] = action,
            ["params"] = parameters,
            ["echo"] = echo,
        });

        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync) _pending[echo] = completion;

        // ClientWebSocket 不支持并发发送，所有 action（发送消息、查询历史、反查引用、群管理）统一排队
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        timeout.CancelAfter(ActionTimeout);

        await _sendGate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeout.Token)
                .ConfigureAwait(false);

            var response = await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);

            var retcode = NapCatMessageParser.GetInt64(response, "retcode");
            if (retcode == 0) return response;

            var reason = NapCatMessageParser.GetString(response, "message")
                         ?? NapCatMessageParser.GetString(response, "wording")
                         ?? "未知错误";
            throw new InvalidOperationException($"NapCat 返回错误（retcode {retcode}）：{reason}");
        }
        finally
        {
            _sendGate.Release();
            lock (_sync) _pending.Remove(echo);
        }
    }
}
