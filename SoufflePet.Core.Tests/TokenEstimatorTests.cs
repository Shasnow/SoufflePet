// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.Collections.Generic;
using Microsoft.Extensions.AI;
using SoufflePet.Services;
using Xunit;

namespace SoufflePet.Core.Tests;

/// <summary>token 估算：中文按字、英文按字符组、消息固定开销。</summary>
public sealed class TokenEstimatorTests
{
    [Fact]
    public void EmptyTextIsZero()
    {
        Assert.Equal(0, TokenEstimator.Estimate(""));
        Assert.Equal(0, TokenEstimator.Estimate(null));
    }

    [Fact]
    public void ChineseCountsPerCharacter()
    {
        Assert.Equal(4, TokenEstimator.Estimate("你好世界"));
    }

    [Fact]
    public void EnglishCountsPerFourCharacters()
    {
        Assert.Equal(2, TokenEstimator.Estimate("abcdefgh"));
        // 向上取整：5 个字符按 2 个 token 计
        Assert.Equal(2, TokenEstimator.Estimate("abcde"));
    }

    [Fact]
    public void LongerTextNeverEstimatesFewerTokens()
    {
        Assert.True(TokenEstimator.Estimate("这是一段比较长的中文文本") >
                    TokenEstimator.Estimate("短"));
    }

    [Fact]
    public void MessagesIncludeOverheadAndToolPayloads()
    {
        var simple = TokenEstimator.EstimateMessages([new ChatMessage(ChatRole.User, "hi")]);
        Assert.Equal(4 + TokenEstimator.Estimate("hi"), simple);

        var withTool = TokenEstimator.EstimateMessages(
        [
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "pwsh", new Dictionary<string, object?> { ["command"] = "dir" })]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "结果")]),
        ]);
        Assert.True(withTool > 8);
    }

    [Fact]
    public void ReasoningContentCounts()
    {
        var withoutReasoning = TokenEstimator.EstimateMessages([new ChatMessage(ChatRole.Assistant, "好的")]);
        var withReasoning = TokenEstimator.EstimateMessages(
        [
            new ChatMessage(ChatRole.Assistant,
                [new TextReasoningContent("想了一长串"), new TextContent("好的")]),
        ]);

        // 思考内容会作为 reasoning_content 每轮重新发给模型，必须计入水位
        Assert.Equal(withoutReasoning + TokenEstimator.Estimate("想了一长串"), withReasoning);
    }

    [Fact]
    public void MediaContentCountsAsFixedOverhead()
    {
        var textOnly = TokenEstimator.EstimateMessages([new ChatMessage(ChatRole.User, "看图")]);
        var withImage = TokenEstimator.EstimateMessages(
        [
            new ChatMessage(ChatRole.User,
                [new TextContent("看图"), new DataContent(new byte[8], "image/png")]),
        ]);

        // 媒体按固定开销计，不按 base64 字节数：否则一张图就会被算成几十万 token
        Assert.InRange(withImage - textOnly, 512, 4096);
    }
}
