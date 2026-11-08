// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SoufflePet.Plugins.QqBot;

/// <summary>
/// QQ 官方机器人 WebSocket 网关：负责建连、Hello/心跳、Identify/Resume、事件分发与断线重连。
/// 只处理 GROUP_AND_C2C_EVENT（群内消息与单聊）对应的事件。
/// </summary>
internal sealed class QqGateway
{
    /// <summary>GROUP_AND_C2C_EVENT：群聊消息（@ 与全量模式）与单聊消息。</summary>
    private const int GroupAndC2cIntent = 1 << 25;

    /// <summary>单帧消息上限，防止异常数据无限占用内存。</summary>
    private const int MaxPayloadBytes = 8 * 1024 * 1024;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>断线后的重连间隔。</summary>
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    private readonly Uri _uri;
    private readonly Func<CancellationToken, Task<string>> _tokenProvider;
    private readonly Func<string, JsonElement, Task> _dispatch;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Lock _sync = new();

    private ClientWebSocket? _socket;
    private string? _sessionId;
    private int _lastSeq;
    private Channel<DispatchEvent>? _dispatchQueue;

    public QqGateway(
        Uri uri,
        Func<CancellationToken, Task<string>> tokenProvider,
        Func<string, JsonElement, Task> dispatch,
        ILogger logger)
    {
        _uri = uri;
        _tokenProvider = tokenProvider;
        _dispatch = dispatch;
        _logger = logger;
    }

    /// <summary>网关主循环；取消令牌触发后退出。</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // 事件串行消费：同一会话的多条消息必须按到达顺序处理，
        // 否则被动回复上下文会被后到的消息覆盖，回复可能挂到错误的 msg_id。
        var queue = Channel.CreateUnbounded<DispatchEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        _dispatchQueue = queue;
        var worker = Task.Run(() => ProcessDispatchQueueAsync(queue.Reader, cancellationToken), CancellationToken.None);

        try
        {
            await ReconnectLoopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            queue.Writer.TryComplete();
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常退出
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "等待 QQ 事件分发退出时出错");
            }

            _dispatchQueue = null;
            _logger.LogInformation("QQ 网关循环已退出");
        }
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var state = new ConnectionState();
            ClientWebSocket? socket = null;
            try
            {
                socket = new ClientWebSocket();
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectTimeout.CancelAfter(ConnectTimeout);
                await socket.ConnectAsync(_uri, connectTimeout.Token).ConfigureAwait(false);

                lock (_sync) _socket = socket;
                _logger.LogInformation("QQ 网关已连接：{Url}", _uri);

                await PumpAsync(socket, state, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "QQ 网关连接异常，{Seconds} 秒后重连", ReconnectDelay.TotalSeconds);
            }
            finally
            {
                if (socket is not null)
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_socket, socket)) _socket = null;
                    }

                    socket.Dispose();
                }
            }

            if (state.InvalidSession || (state.ResumeAttempted && !state.SawDispatch))
            {
                _sessionId = null;
                _lastSeq = 0;
            }

            if (cancellationToken.IsCancellationRequested) break;

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

    /// <summary>关闭当前连接，供插件停用/退出时调用。</summary>
    public async Task CloseAsync()
    {
        ClientWebSocket? socket;
        lock (_sync) socket = _socket;
        if (socket is not { State: WebSocketState.Open }) return;

        try
        {
            using var timeout = new CancellationTokenSource(CloseTimeout);
            await socket
                .CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "shutdown", timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "关闭 QQ 网关时出错");
        }
    }

    private async Task PumpAsync(ClientWebSocket socket, ConnectionState state, CancellationToken cancellationToken)
    {
        var heartbeatCts = new CancellationTokenSource();
        Task? heartbeat = null;
        try
        {
            while (true)
            {
                var payload = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
                if (payload is null) return;

                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                var op = QqJson.GetInt(root, "op");

                switch (op)
                {
                    case 10:
                    {
                        var interval = QqJson.GetInt(root, "d", "heartbeat_interval") ?? 45000;
                        if (heartbeat is null)
                        {
                            var delay = TimeSpan.FromMilliseconds(Math.Clamp(interval, 10000, 120000));
                            heartbeat = HeartbeatLoopAsync(socket, delay, heartbeatCts.Token);
                        }

                        await AuthenticateAsync(socket, state, cancellationToken).ConfigureAwait(false);
                        break;
                    }

                    case 0:
                        HandleDispatch(root, state);
                        break;

                    case 11:
                        // 心跳 ACK，无需处理
                        break;

                    case 7:
                        _logger.LogInformation("QQ 网关要求重连");
                        return;

                    case 9:
                        _logger.LogWarning("QQ 网关会话失效，将重新鉴权");
                        state.InvalidSession = true;
                        return;

                    default:
                        break;
                }
            }
        }
        finally
        {
            heartbeatCts.Cancel();
            if (heartbeat is not null)
            {
                try
                {
                    await heartbeat.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "等待 QQ 网关心跳退出时出错");
                }
            }

            heartbeatCts.Dispose();
        }
    }

    private async Task AuthenticateAsync(
        ClientWebSocket socket,
        ConnectionState state,
        CancellationToken cancellationToken)
    {
        var token = "QQBot " + await _tokenProvider(cancellationToken).ConfigureAwait(false);
        var canResume = _sessionId is { Length: > 0 };

        state.ResumeAttempted = canResume;
        var payload = canResume
            ? new Dictionary<string, object?>
            {
                ["op"] = 6,
                ["d"] = new Dictionary<string, object?>
                {
                    ["token"] = token,
                    ["session_id"] = _sessionId,
                    ["seq"] = _lastSeq,
                },
            }
            : new Dictionary<string, object?>
            {
                ["op"] = 2,
                ["d"] = new Dictionary<string, object?>
                {
                    ["token"] = token,
                    ["intents"] = GroupAndC2cIntent,
                    ["shard"] = new[] { 0, 1 },
                },
            };

        await SendAsync(socket, payload, cancellationToken).ConfigureAwait(false);

        if (canResume)
            _logger.LogInformation("QQ 网关已发送 Resume（seq {Seq}）", _lastSeq);
        else
            _logger.LogInformation("QQ 网关已发送 Identify（intents {Intents}）", GroupAndC2cIntent);
    }

    private void HandleDispatch(JsonElement root, ConnectionState state)
    {
        if (QqJson.GetInt(root, "s") is { } seq) _lastSeq = seq;

        var eventName = QqJson.GetString(root, "t");
        if (string.IsNullOrEmpty(eventName)) return;

        var data = root.TryGetProperty("d", out var value) ? value : default;
        if (data.ValueKind != JsonValueKind.Object) return;

        var cloned = data.Clone();

        if (string.Equals(eventName, "READY", StringComparison.Ordinal))
        {
            state.SawDispatch = true;
            if (QqJson.GetString(cloned, "session_id") is { Length: > 0 } session)
            {
                _sessionId = session;
                _logger.LogInformation("QQ 网关 READY，会话 {Session}", session);
            }

            return;
        }

        state.SawDispatch = true;
        if (_dispatchQueue is { } queue)
        {
            queue.Writer.TryWrite(new DispatchEvent(eventName, cloned));
        }
        else
        {
            _logger.LogDebug("QQ 网关尚未启动分发队列，忽略事件 {Event}", eventName);
        }
    }

    /// <summary>串行消费事件队列；取消时立即退出，由 RunAsync 负责等待。</summary>
    private async Task ProcessDispatchQueueAsync(
        ChannelReader<DispatchEvent> reader,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await _dispatch(item.Name, item.Data).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "处理 QQ 事件 {Event} 失败", item.Name);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 正常退出
        }
    }

    /// <summary>队列元素：事件名与已克隆的事件体。</summary>
    private readonly record struct DispatchEvent(string Name, JsonElement Data);

    private async Task HeartbeatLoopAsync(ClientWebSocket socket, TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var payload = new Dictionary<string, object?>
                {
                    ["op"] = 1,
                    ["d"] = _lastSeq,
                };
                await SendAsync(socket, payload, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "QQ 网关心跳发送失败");
        }
    }

    private async Task SendAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket
                .SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private static async Task<string?> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;

            stream.Write(buffer, 0, result.Count);
            if (stream.Length > MaxPayloadBytes)
                throw new InvalidOperationException("QQ 网关单帧消息超过上限");

            if (result.EndOfMessage) break;
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>单次连接的会话状态，用于判断是否需要放弃 Resume。</summary>
    private sealed class ConnectionState
    {
        public bool ResumeAttempted;
        public bool SawDispatch;
        public bool InvalidSession;
    }
}
