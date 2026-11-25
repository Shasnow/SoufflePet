// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SoufflePet.Plugin.Sdk;

/// <summary>
/// QQ 接口与多媒体 CDN 下载共用的 HttpClient 工厂：IPv4 优先建连、不设整体超时。
/// 本机 IPv6 出口不通时，.NET 默认会优先尝试 IPv6、把请求卡到超时，因此这里只走
/// IPv4（没有 A 记录才退回全部地址）。同一域名下又常混着连不通的 IPv4 地址，
/// 所以候选地址是并发尝试、先连上的胜出，不再赌 DNS 返回的第一个地址。
/// 整体超时交给各调用方按场景控制，这里只保留 15 秒的连接超时。
/// 两个 QQ 平台插件原本各写了一份同样的实现，统一收敛到此处。
/// </summary>
public static class Ipv4HttpClient
{
    /// <summary>QQ CDN 对没有 UA 的请求会长时间不响应，统一带上 UA。</summary>
    public const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SoufflePet/1.0";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>创建一个 IPv4 优先、无整体超时、带 UA 的 HttpClient；调用方负责释放。</summary>
    public static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = ConnectTimeout,
            ConnectCallback = ConnectIpv4Async,
        };

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(DefaultUserAgent);
        return client;
    }

    /// <summary>
    /// 筛出要尝试的候选地址：有 IPv4 就只试 IPv4（本机 IPv6 出口常不通），
    /// 没有 IPv4 才原样退回全部地址。返回顺序只是 DNS 原序，不代表尝试优先级。
    /// </summary>
    internal static IReadOnlyList<IPAddress> FilterIpv4Candidates(IPAddress[] addresses)
    {
        var ipv4 = Array.FindAll(addresses, item => item.AddressFamily == AddressFamily.InterNetwork);
        return ipv4.Length > 0 ? ipv4 : addresses;
    }

    private static async ValueTask<Stream> ConnectIpv4Async(
        SocketsHttpConnectionContext context,
        CancellationToken token)
    {
        var addresses = await Dns
            .GetHostAddressesAsync(context.DnsEndPoint.Host, token)
            .ConfigureAwait(false);

        return await ConnectAsync(context.DnsEndPoint, addresses, token, TryConnectAsync)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 并发尝试全部候选地址，谁先连上就用谁，其余立刻取消并释放。
    /// 地址来源与连接动作都可注入，便于脱离真实网络验证竞速、取消与失败聚合。
    /// </summary>
    internal static async Task<Stream> ConnectAsync(
        DnsEndPoint endPoint,
        IPAddress[] addresses,
        CancellationToken token,
        Func<IPAddress, int, CancellationToken, Task<Socket?>> connect)
    {
        var candidates = FilterIpv4Candidates(addresses);
        if (candidates.Count == 0)
            throw new HttpRequestException($"无法解析主机 {endPoint.Host}");

        // QQ CDN 的 A 记录里会混入连不通的地址（TCP 无响应），只赌第一个会白等到连接超时。
        // 这里并发尝试全部候选，谁先连上就用谁，其余立刻取消。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        var attempts = new List<Task<Socket?>>(candidates.Count);
        foreach (var address in candidates)
            attempts.Add(connect(address, endPoint.Port, linked.Token));

        Exception? firstFailure = null;
        while (attempts.Count > 0)
        {
            var completed = await Task.WhenAny(attempts).ConfigureAwait(false);
            attempts.Remove(completed);

            Socket? socket;
            try
            {
                socket = await completed.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 取消不算失败原因，循环结束后按调用方 token 统一抛 OCE
                if (ex is not OperationCanceledException) firstFailure ??= ex;
                continue;
            }

            if (socket is null) continue;

            linked.Cancel();
            DisposeRemaining(attempts);
            return new NetworkStream(socket, ownsSocket: true);
        }

        token.ThrowIfCancellationRequested();
        throw new HttpRequestException(
            $"无法连接到 {endPoint.Host}（已尝试 {candidates.Count} 个地址）", firstFailure);
    }

    /// <summary>
    /// 尝试连接一个地址；失败时关掉 socket 并把异常交给 ConnectAsync 汇总成失败原因。
    /// 仍在进行中的尝试由 DisposeRemaining 兜底观察，不会产生未观察的异常。
    /// </summary>
    private static async Task<Socket?> TryConnectAsync(IPAddress address, int port, CancellationToken token)
    {
        Socket? socket = null;
        try
        {
            socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await socket
                .ConnectAsync(new IPEndPoint(address, port), token)
                .ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 胜出的连接确定后清理其余尝试：已连上的 socket 要关掉，还在连的等它结束再关，
    /// 否则并发连接全部成功时会泄漏未使用的 socket；失败的尝试也在这里观察异常。
    /// </summary>
    private static void DisposeRemaining(List<Task<Socket?>> attempts)
    {
        foreach (var attempt in attempts)
        {
            _ = attempt.ContinueWith(
                static task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion)
                    {
                        task.Result?.Dispose();
                    }
                    else if (task.IsFaulted)
                    {
                        _ = task.Exception; // 主动观察，避免 UnobservedTaskException
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
