// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;
using SoufflePet.Chat;
using Xunit;

namespace SoufflePet.Core.Tests;

/// <summary>上下文压缩的纯逻辑：水位判断、按 token 预算拆分、摘要挂载位置、压缩指令内容。</summary>
public sealed class ContextCompactionTests
{
    private static ChatMessage User(string text) => new(ChatRole.User, text);

    private static ChatMessage Assistant(string text) => new(ChatRole.Assistant, text);

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    [InlineData(100_000)]
    public void ShouldCompress_NeverTriggersWithoutWindow(int estimated)
    {
        // 窗口为 0（未设置）时永不触发
        Assert.False(ContextCompaction.ShouldCompress(estimated, 0));
    }

    [Fact]
    public void KeepTokens_IsHalfTheWindowWithAFloor()
    {
        Assert.Equal(65_536, ContextCompaction.KeepTokens(128 * 1024));
        Assert.Equal(32_768, ContextCompaction.KeepTokens(64 * 1024));
        // 窗口很小时留出下限，避免历史只剩一段摘要
        Assert.Equal(ContextCompaction.MinKeepTokens, ContextCompaction.KeepTokens(1024));
        Assert.Equal(0, ContextCompaction.KeepTokens(0));
    }

    [Fact]
    public void SplitIndex_IsZeroWhenEverythingFitsTheBudget()
    {
        List<ChatMessage> history = [User("q1"), Assistant("a1"), User("q2"), Assistant("a2")];

        // 没有可摘要的部分 → 0（调用方据此判定「无需压缩」）
        Assert.Equal(0, ContextCompaction.SplitIndex(history, 10_000));
    }

    [Fact]
    public void SplitIndex_KeepsOnlyTheTurnsWithinBudget()
    {
        // 第一轮巨大（约 25K token），预算 1K → 分界落在最后一轮提问上
        List<ChatMessage> history =
            [User(new string('a', 100_000)), Assistant("a1"), User("q2"), Assistant("a2")];

        var split = ContextCompaction.SplitIndex(history, 1_000);

        Assert.Equal(2, split);
        Assert.Equal(ChatRole.User, history[split].Role);
        Assert.StartsWith("q2", history[split].Text);
    }

    [Fact]
    public void SplitIndex_NeverCutsInsideATurn()
    {
        // 预算很小：分界仍落在用户消息上，工具调用与结果不会被拆散
        List<ChatMessage> history =
        [
            User("q1"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "read_file")]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", new string('x', 40_000))]),
            Assistant("a1"),
            User("q2"),
            Assistant("a2"),
        ];

        var split = ContextCompaction.SplitIndex(history, 1_000);

        Assert.Equal(4, split);
        var tail = history.Skip(split).ToList();
        Assert.All(tail, message => Assert.NotEqual(ChatRole.Tool, message.Role));
    }

    [Fact]
    public void SplitIndex_KeepsTheLastTurnEvenWhenItAloneExceedsBudget()
    {
        // 单轮自身超预算时仍保留这一轮：拆掉本轮提问只会让请求本身不可用
        List<ChatMessage> history = [User("q1"), Assistant("a1"), User(new string('a', 100_000))];

        var split = ContextCompaction.SplitIndex(history, 1_000);

        Assert.Equal(2, split);
        Assert.Equal(ChatRole.User, history[split].Role);
    }

    [Fact]
    public void SplitIndex_ReturnsZeroWithoutUserMessages()
    {
        List<ChatMessage> history = [Assistant("只有助手消息")];

        Assert.Equal(0, ContextCompaction.SplitIndex(history, 1_000));
        Assert.Equal(0, ContextCompaction.SplitIndex([], 1_000));
    }

    [Fact]
    public void ShouldCompress_TriggersAtNinetyPercent()
    {
        // 窗口 10000：90% = 9000，减去预留 4096 → 估算到 4904 时触发
        Assert.False(ContextCompaction.ShouldCompress(4903, 10000));
        Assert.True(ContextCompaction.ShouldCompress(4904, 10000));
        Assert.True(ContextCompaction.ShouldCompress(20000, 10000));
    }

    [Fact]
    public void WrapSummary_UsesConversationSummaryTag()
    {
        var block = ContextCompaction.WrapSummary("  要点一\n要点二  ");

        Assert.StartsWith("<conversation_summary>", block);
        Assert.EndsWith("</conversation_summary>", block);
        Assert.Contains("要点一", block);
        Assert.DoesNotContain("  要点一", block);
    }

    [Fact]
    public void AttachSummary_PrependsToFirstUserMessage()
    {
        List<ChatMessage> history =
        [
            new(ChatRole.Assistant, "开场白"),
            new(ChatRole.User, "原问题"),
            new(ChatRole.Assistant, "原回答"),
        ];

        var result = ContextCompaction.AttachSummary(history, "<conversation_summary>摘要</conversation_summary>");

        Assert.Equal(3, result.Count);
        Assert.Equal(ChatRole.Assistant, result[0].Role);
        Assert.Equal(ChatRole.User, result[1].Role);
        Assert.Contains("<conversation_summary>摘要</conversation_summary>", result[1].Text);
        Assert.Contains("原问题", result[1].Text);
        Assert.Equal("原回答", result[2].Text);
    }

    [Fact]
    public void AttachSummary_InsertsUserMessageWhenNoneExists()
    {
        List<ChatMessage> history = [new(ChatRole.Assistant, "只有助手消息")];

        var result = ContextCompaction.AttachSummary(history, "<conversation_summary>摘要</conversation_summary>");

        Assert.Equal(2, result.Count);
        Assert.Equal(ChatRole.User, result[0].Role);
        Assert.Contains("摘要", result[0].Text);
        Assert.Equal("只有助手消息", result[1].Text);
    }
}
