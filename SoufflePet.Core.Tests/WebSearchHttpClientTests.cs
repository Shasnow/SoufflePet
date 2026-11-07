// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SoufflePet.Services;
using Xunit;

namespace SoufflePet.Core.Tests;

/// <summary>
/// 双栈竞速连接：模拟「IPv6 黑洞排第一、IPv4 秒连」，验证首次搜索不再被拖到超时。
/// </summary>
public sealed class WebSearchHttpClientTests
{
    [Fact]
    public void PrimaryHandlerIsSocketsHandlerWithConnectCallback()
    {
        using var handler = WebSearchHttpClient.CreatePrimaryHandler();
        var sockets = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.NotNull(sockets.ConnectCallback);
    }

    [Fact]
    public async Task WinnerReturnsImmediatelyAndBlackholeIsCancelled()
    {
        using var race = new CancellationTokenSource();
        using var winner = new MemoryStream();

        // 排第一的「IPv6」永不主动完成：只有被取消时才结算（等价于 socket 被 Dispose）。
        var blackhole = new TaskCompletionSource<WebSearchHttpClient.ConnectionAttempt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        race.Token.Register(() => blackhole.TrySetResult(
            new WebSearchHttpClient.ConnectionAttempt(null, new OperationCanceledException())));

        var attempts = new List<Task<WebSearchHttpClient.ConnectionAttempt>>
        {
            blackhole.Task,
            Task.FromResult(new WebSearchHttpClient.ConnectionAttempt(winner, null)),
        };

        var result = await WebSearchHttpClient.RaceAsync(attempts, race, "「test:443」");

        Assert.Same(winner, result);
        Assert.True(race.IsCancellationRequested, "落败的黑洞连接必须立刻被取消，而不是等 21 秒");
        Assert.True(blackhole.Task.IsCompleted);
    }

    [Fact]
    public async Task ThrowsWithInnerFailureWhenEveryAddressFails()
    {
        using var race = new CancellationTokenSource();
        var failure = new SocketException((int)SocketError.TimedOut);
        var attempts = new List<Task<WebSearchHttpClient.ConnectionAttempt>>
        {
            Task.FromResult(new WebSearchHttpClient.ConnectionAttempt(null, failure)),
            Task.FromResult(new WebSearchHttpClient.ConnectionAttempt(null, new IOException("unreachable"))),
        };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => WebSearchHttpClient.RaceAsync(attempts, race, "「test:443」").AsTask());

        Assert.Contains("2 个地址", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task LoserStreamIsDisposedIfItAlsoConnected()
    {
        using var race = new CancellationTokenSource();
        using var winner = new MemoryStream();
        using var loserStream = new TrackingStream();

        // 落败地址在竞速取消后才「碰巧连上」，其 stream 必须被回收，不能泄漏 socket。
        var loser = new TaskCompletionSource<WebSearchHttpClient.ConnectionAttempt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        race.Token.Register(() => loser.TrySetResult(
            new WebSearchHttpClient.ConnectionAttempt(loserStream, null)));

        var attempts = new List<Task<WebSearchHttpClient.ConnectionAttempt>>
        {
            loser.Task,
            Task.FromResult(new WebSearchHttpClient.ConnectionAttempt(winner, null)),
        };

        var result = await WebSearchHttpClient.RaceAsync(attempts, race, "「test:443」");
        Assert.Same(winner, result);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!loserStream.Disposed && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(loserStream.Disposed, "落败但已连上的 stream 应被回收");
    }

    private sealed class TrackingStream : MemoryStream
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
