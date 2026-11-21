// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SoufflePet.Plugin.Sdk;
using SoufflePet.Services;
using Xunit;

namespace SoufflePet.Core.Tests;

/// <summary>
/// 平台发送的结果语义：失败原因以返回值表达（<see cref="PlatformSendResult.Ok" /> = false），
/// 调用方据此记日志或提示，不必靠匹配返回文本猜成败。平台命令的回执依赖这一点——
/// 只 catch 异常会让「平台没接入 / 会话 id 为空」这类失败彻底静默。
/// </summary>
public sealed class PlatformSendResultTests
{
    [Fact]
    public async Task NoPlatformRegisteredFails()
    {
        using var platforms = new PlatformService();

        var result = await platforms.TrySendMessageAsync("napcat", "group:1", "hi");

        Assert.False(result.Ok);
        Assert.Contains("没有可用的消息平台", result.Message);
    }

    [Fact]
    public async Task UnknownPlatformFails()
    {
        using var platforms = new PlatformService();
        platforms.Register(new RecordingPlatform("napcat"));

        var result = await platforms.TrySendMessageAsync("qqbot", "group:1", "hi");

        Assert.False(result.Ok);
        Assert.Contains("没有找到消息平台", result.Message);
        Assert.Contains("napcat", result.Message);
    }

    [Fact]
    public async Task EmptyConversationOrMessageFails()
    {
        using var platforms = new PlatformService();
        var platform = new RecordingPlatform("napcat");
        platforms.Register(platform);

        var emptyConversation = await platforms.TrySendMessageAsync("napcat", "  ", "hi");
        Assert.False(emptyConversation.Ok);
        Assert.Contains("会话 id", emptyConversation.Message);

        var emptyMessage = await platforms.TrySendMessageAsync("napcat", "group:1", "  ");
        Assert.False(emptyMessage.Ok);
        Assert.Contains("消息内容", emptyMessage.Message);

        Assert.Empty(platform.Sent);
    }

    [Fact]
    public async Task SuccessfulSendReportsOkAndReachesPlatform()
    {
        using var platforms = new PlatformService();
        var platform = new RecordingPlatform("napcat");
        platforms.Register(platform);

        var result = await platforms.TrySendMessageAsync(" NAPCAT ", "group:1", "已切换模型");

        Assert.True(result.Ok);
        Assert.Contains("已向平台", result.Message);
        var sent = Assert.Single(platform.Sent);
        Assert.Equal("group:1", sent.ConversationId);
        Assert.Equal("已切换模型", sent.Text);
    }

    [Fact]
    public async Task StringOverloadStillReturnsReadableText()
    {
        using var platforms = new PlatformService();
        platforms.Register(new RecordingPlatform("napcat"));

        var text = await platforms.SendMessageAsync("napcat", "group:1", "hi");

        Assert.Contains("已向平台", text);
        Assert.Contains("group:1", text);
    }

    [Fact]
    public async Task PluginExceptionPropagatesSoCallerCanLogIt()
    {
        using var platforms = new PlatformService();
        platforms.Register(new ThrowingPlatform("napcat"));

        // 插件自身失败仍以异常上报；平台命令回执那边会 catch 并写日志
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => platforms.TrySendMessageAsync("napcat", "group:1", "hi"));
    }

    private class RecordingPlatform(string id) : IMessagePlatform
    {
        private readonly List<(string ConversationId, string Text)> _sent = [];

        public string Id { get; } = id;

        public string? PlatformPrompt => null;

        public event EventHandler<PlatformIncomingMessage>? OnMentioned
        {
            add { }
            remove { }
        }

        public IReadOnlyList<(string ConversationId, string Text)> Sent
        {
            get { lock (_sent) return [.. _sent]; }
        }

        public Task<IReadOnlyList<PlatformConversation>> GetConversationsAsync() =>
            Task.FromResult<IReadOnlyList<PlatformConversation>>([]);

        public Task<string> GetMessagesAsync(string conversationId, int limit = 20) =>
            Task.FromResult(string.Empty);

        public virtual Task SendMessageAsync(string conversationId, string message)
        {
            lock (_sent) _sent.Add((conversationId, message));
            return Task.CompletedTask;
        }

        public Task SendAttachmentAsync(string conversationId, PlatformAttachment attachment) =>
            Task.CompletedTask;
    }

    private sealed class ThrowingPlatform(string id) : RecordingPlatform(id)
    {
        public override Task SendMessageAsync(string conversationId, string message) =>
            throw new InvalidOperationException("平台离线");
    }
}
