// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SoufflePet.Plugin.Sdk;
using Xunit;

namespace SoufflePet.Core.Tests;

/// <summary>QQ 平台插件共享的 IPv4 优先 HttpClient：候选筛选、并发建连与工厂配置。</summary>
public sealed class Ipv4HttpClientTests
{
    private static readonly IPAddress V6 = IPAddress.Parse("240e:ff:e020:9be::1");
    private static readonly IPAddress V4 = IPAddress.Parse("14.215.183.202");
    private static readonly IPAddress V4Other = IPAddress.Parse("14.215.183.173");
    private static readonly DnsEndPoint EndPoint = new("cdn.example.com", 443);

    [Fact]
    public void FilterIpv4CandidatesKeepsAllIpv4AndDropsIpv6()
        => Assert.Equal([V4, V4Other], Ipv4HttpClient.FilterIpv4Candidates([V6, V4, V4Other]));

    [Fact]
    public void FilterIpv4CandidatesReturnsAllAddressesWhenNoIpv4Exists()
        => Assert.Equal([V6], Ipv4HttpClient.FilterIpv4Candidates([V6]));

    [Fact]
    public async Task ConnectThrowsWhenDnsReturnsNoAddress()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Ipv4HttpClient.ConnectAsync(EndPoint, [], CancellationToken.None, NeverConnect));
        Assert.Contains("cdn.example.com", ex.Message);
    }

    [Fact]
    public async Task ConnectKeepsFirstConnectedAndDisposesLoser()
    {
        using var winner = await ConnectLoopbackAsync();
        using var loser = FakeSocket();

        var stream = await Ipv4HttpClient.ConnectAsync(
            EndPoint,
            [V4, V4Other],
            CancellationToken.None,
            (address, _, _) => address.Equals(V4)
                ? Task.FromResult<Socket?>(winner)
                : SlowSocket(loser));

        using (stream)
            Assert.Same(winner, ((NetworkStream)stream).Socket);

        // 败者在取消之后才连上，也要被 DisposeRemaining 关掉
        await WaitUntilAsync(() => loser.SafeHandle.IsClosed);
        Assert.True(loser.SafeHandle.IsClosed);
    }

    [Fact]
    public async Task ConnectReportsAttemptCountWhenAllAddressesFail()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Ipv4HttpClient.ConnectAsync(
                EndPoint,
                [V4, V4Other],
                CancellationToken.None,
                NeverConnect));
        Assert.Contains("2 个地址", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task ConnectKeepsSocketFailureAsInnerException()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Ipv4HttpClient.ConnectAsync(
                EndPoint,
                [V4, V4Other],
                CancellationToken.None,
                (address, _, _) => address.Equals(V4)
                    ? Task.FromException<Socket?>(new SocketException((int)SocketError.ConnectionRefused))
                    : NeverConnect(address, 0, CancellationToken.None)));
        Assert.IsType<SocketException>(ex.InnerException);
    }

    [Fact]
    public async Task ConnectPropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Ipv4HttpClient.ConnectAsync(
                EndPoint,
                [V4],
                cts.Token,
                (_, _, token) => Task.FromCanceled<Socket?>(token)));
    }

    [Fact]
    public void CreateDisablesOverallTimeoutAndSetsUserAgent()
    {
        using var client = Ipv4HttpClient.Create();

        // 整体超时不由工厂设置，交给各调用方按场景控制；CDN 需要 UA 才会正常响应。
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
        Assert.Contains("SoufflePet", client.DefaultRequestHeaders.UserAgent.ToString());
    }

    private static Task<Socket?> NeverConnect(IPAddress address, int port, CancellationToken token)
        => Task.FromResult<Socket?>(null);

    private static async Task<Socket?> SlowSocket(Socket socket)
    {
        await Task.Delay(30).ConfigureAwait(false);
        return socket;
    }

    private static Socket FakeSocket()
        => new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

    /// <summary>建一个已连上本机回环端口的 socket：NetworkStream 要求 socket 处于已连接状态。</summary>
    private static async Task<Socket> ConnectLoopbackAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var socket = FakeSocket();
        await socket.ConnectAsync((IPEndPoint)listener.LocalEndpoint).ConfigureAwait(false);
        return socket;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(10).ConfigureAwait(false);
    }
}
