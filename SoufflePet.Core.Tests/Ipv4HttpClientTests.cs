// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.Net;
using System.Net.Http;
using SoufflePet.Plugin.Sdk;
using Xunit;

namespace SoufflePet.Core.Tests;

/// <summary>QQ 平台插件共享的 IPv4 优先 HttpClient：选址规则与工厂配置。</summary>
public sealed class Ipv4HttpClientTests
{
    private static readonly IPAddress V6 = IPAddress.Parse("240e:ff:e020:9be::1");
    private static readonly IPAddress V4 = IPAddress.Parse("14.215.183.202");

    [Fact]
    public void PicksFirstIpv4EvenWhenIpv6ComesFirst()
        => Assert.Equal(V4, Ipv4HttpClient.SelectAddress([V6, V4, IPAddress.Parse("14.215.183.173")], "host"));

    [Fact]
    public void FallsBackToFirstAddressWhenOnlyIpv6Exists()
        => Assert.Equal(V6, Ipv4HttpClient.SelectAddress([V6], "host"));

    [Fact]
    public void ThrowsWhenDnsReturnsNoAddress()
    {
        var ex = Assert.Throws<HttpRequestException>(
            () => Ipv4HttpClient.SelectAddress([], "api.example.com"));
        Assert.Contains("api.example.com", ex.Message);
    }

    [Fact]
    public void CreateDisablesOverallTimeoutAndSetsUserAgent()
    {
        using var client = Ipv4HttpClient.Create();

        // 整体超时不由工厂设置，交给各调用方按场景控制；CDN 需要 UA 才会正常响应。
        Assert.Equal(System.Threading.Timeout.InfiniteTimeSpan, client.Timeout);
        Assert.Contains("SoufflePet", client.DefaultRequestHeaders.UserAgent.ToString());
    }
}
