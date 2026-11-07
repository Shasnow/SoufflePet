// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SoufflePet.Plugin.Sdk;

/// <summary>
/// QQ 接口与多媒体 CDN 下载共用的 HttpClient 工厂：IPv4 优先建连、不设整体超时。
/// 本机 IPv6 出口不通时，.NET 默认会优先尝试 IPv6、把请求卡到超时，因此这里显式只走
/// IPv4（没有 A 记录才退回首个地址）。整体超时交给各调用方按场景控制，这里只保留
/// 15 秒的连接超时。两个 QQ 平台插件原本各写了一份同样的实现，统一收敛到此处。
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

        var client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(DefaultUserAgent);
        return client;
    }

    /// <summary>
    /// 从 DNS 结果里挑第一个 IPv4 地址；只有 IPv6 时退回首个地址，解析不到则报错。
    /// 抽成独立函数便于脱离真实网络验证选址规则。
    /// </summary>
    internal static IPAddress SelectAddress(IPAddress[] addresses, string host)
    {
        if (addresses.Length == 0)
            throw new HttpRequestException($"无法解析主机 {host}");

        return Array.Find(addresses, item => item.AddressFamily == AddressFamily.InterNetwork)
               ?? addresses[0];
    }

    private static async ValueTask<Stream> ConnectIpv4Async(
        SocketsHttpConnectionContext context,
        CancellationToken token)
    {
        var addresses = await Dns
            .GetHostAddressesAsync(context.DnsEndPoint.Host, token)
            .ConfigureAwait(false);

        var address = SelectAddress(addresses, context.DnsEndPoint.Host);
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket
                .ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token)
                .ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
