// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.Net;
using System.Net.Sockets;

namespace SoufflePet.Services;

/// <summary>
/// 联网搜索客户端的主 HTTP handler。
/// 修复「重启后首次 web_search 必然超时」：DNS 把 qianfan.baidubce.com 的 IPv6 排在前面，
/// 但该主机的 IPv6 是黑洞（SYN 无响应），默认连接会串行死等约 21 秒才回退 IPv4，超过 20 秒
/// 请求超时；超时后 IPv4 连接其实已悄悄建好进了连接池，于是第二次调用反而秒回。
/// 这里改用自定义 ConnectCallback 对所有解析出的地址并发发起连接、取最先成功者，
/// 黑洞地址不再拖住整次请求。修复必须放在 handler 层：IHttpClientFactory 每 2 分钟轮换一次
/// handler、连接池随之失效，只在启动时「预热一次」覆盖不了后续的每一次冷连接。
/// </summary>
public static class WebSearchHttpClient
{
    /// <summary>创建带双栈竞速连接的主 handler，供命名 HttpClient 使用。</summary>
    public static HttpMessageHandler CreatePrimaryHandler() => new SocketsHttpHandler
    {
        ConnectCallback = ConnectAsync,
    };

    /// <summary>一次地址连接的结果：成功带回 stream，失败带回异常。</summary>
    internal readonly record struct ConnectionAttempt(Stream? Stream, Exception? Failure);

    /// <summary>
    /// SocketsHttpHandler 的连接回调：解析域名后向所有地址并发连接，取最先成功的一条。
    /// 竞速结束（无论成败）都取消落败的连接，避免黑洞地址悬挂到操作系统超时。
    /// </summary>
    internal static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;
        var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0)
            throw new HttpRequestException($"无法解析主机「{endpoint.Host}」。");

        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var attempts = addresses
            .Select(address => TryConnect(address, endpoint.Port, race.Token))
            .ToList();
        return await RaceAsync(attempts, race, $"「{endpoint.Host}:{endpoint.Port}」").ConfigureAwait(false);
    }

    /// <summary>
    /// 从并发的连接任务里取第一个成功者并立刻返回；全部失败时抛出带末次原因的异常。
    /// 抽成对任务列表的操作，便于脱离真实 DNS/网络验证竞速与取消行为。
    /// </summary>
    internal static async ValueTask<Stream> RaceAsync(
        IReadOnlyList<Task<ConnectionAttempt>> attempts,
        CancellationTokenSource race,
        string target)
    {
        Exception? lastFailure = null;
        try
        {
            var remaining = new List<Task<ConnectionAttempt>>(attempts);
            while (remaining.Count > 0)
            {
                var finished = await Task.WhenAny(remaining).ConfigureAwait(false);
                remaining.Remove(finished);
                var result = await finished.ConfigureAwait(false);
                if (result.Stream is not null)
                {
                    // 胜出后不等待落败任务：finally 里 race.Cancel() 会关掉它们的 socket；
                    // 万一某条也刚好连上了，交给 ObserveLosers 回收，避免泄漏 socket。
                    ObserveLosers(remaining);
                    return result.Stream;
                }
                lastFailure = result.Failure ?? lastFailure;
            }
        }
        finally
        {
            race.Cancel();
        }

        throw new HttpRequestException($"无法连接到 {target}（已尝试 {attempts.Count} 个地址）。", lastFailure);
    }

    /// <summary>回收竞速落败但碰巧也连上了的 stream；TryConnect 已吞掉异常，这里只需处理结果。</summary>
    private static void ObserveLosers(IEnumerable<Task<ConnectionAttempt>> losers)
    {
        foreach (var task in losers)
        {
            _ = task.ContinueWith(
                static completed =>
                {
                    if (completed.IsCompletedSuccessfully && completed.Result.Stream is { } stream)
                        stream.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    /// <summary>连接单个地址；失败时关闭 socket，把异常收进结果而不外抛。</summary>
    private static async Task<ConnectionAttempt> TryConnect(
        IPAddress address, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            // 竞速落败被取消时立刻关闭 socket，不等操作系统约 21 秒的连接超时。
            using var registration = cancellationToken.Register(
                static state => ((Socket)state!).Dispose(), socket);
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
            // 所有权移交给 NetworkStream，返回后不再由这里 Dispose。
            return new ConnectionAttempt(new NetworkStream(socket, ownsSocket: true), null);
        }
        catch (Exception ex)
        {
            socket.Dispose();
            return new ConnectionAttempt(null, ex);
        }
    }
}
