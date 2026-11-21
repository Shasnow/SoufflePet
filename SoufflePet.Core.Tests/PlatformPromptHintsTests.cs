// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using SoufflePet.Services;
using Xunit;

namespace SoufflePet.Core.Tests;

/// <summary>
/// 平台消息的模型文案：正文只有纯正文，会话 id / 「@ 了你」/ 「引用」都在
/// <c>&lt;platform_message&gt;</c> 的属性里。系统提示词负责讲清这些属性怎么用，
/// 工具参数只说明会话 id 的写法——两处措辞保持一致，模型才不会回错会话或不敢回复。
/// </summary>
public sealed class PlatformPromptHintsTests
{
    [Fact]
    public void GuideExplainsConversationAttributeAndHowToReply()
    {
        var guide = PlatformPromptHints.PlatformMessageGuide;

        Assert.Contains("<platform_message>", guide);
        Assert.Contains("conversation", guide);
        Assert.Contains("sender", guide);
        Assert.Contains("send_platform_message", guide);
        Assert.Contains("get_platform_messages", guide);
        // 装饰（@ 了你 / 引用）也必须是属性，模型才知道怎么解读
        Assert.Contains("mentions_self", guide);
        Assert.Contains("quote", guide);
        // 提示词是模型直接读的文本：不能出现转义残留（\" 会被当成字面量）
        Assert.DoesNotContain("\\\"", guide);
    }

    [Fact]
    public void ConversationParameterExplainsIdFormat()
    {
        var hint = PlatformPromptHints.ConversationParameter;

        Assert.Contains("会话 id", hint);
        Assert.Contains("group:12345", hint);
        Assert.Contains("private:777", hint);
    }

    [Fact]
    public void PromptTextNeverClaimConversationComesFromBodyPrefix()
    {
        // 旧文案是「会话 id 取自平台消息前缀」：正文去前缀后这句就是错的
        Assert.DoesNotContain("消息前缀", PlatformPromptHints.PlatformMessageGuide);
        Assert.DoesNotContain("前缀", PlatformPromptHints.ConversationParameter);
    }

    [Theory]
    [InlineData("GetPlatformMessages")]
    [InlineData("SendPlatformMessage")]
    [InlineData("SendPlatformAttachment")]
    public void PlatformToolsUseTheSharedConversationHint(string methodName)
    {
        var method = typeof(ChatTools).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var conversation = method!.GetParameters()
            .SingleOrDefault(parameter => parameter.Name == "conversation");
        Assert.NotNull(conversation);

        var description = conversation!.GetCustomAttribute<DescriptionAttribute>();
        Assert.NotNull(description);
        // 三个平台工具的 conversation 参数必须复用同一份说明，避免各自漂移
        Assert.Equal(PlatformPromptHints.ConversationParameter, description!.Description);
    }
}
