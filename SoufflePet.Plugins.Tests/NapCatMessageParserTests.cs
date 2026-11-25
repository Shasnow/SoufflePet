// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.Text.Json;
using SoufflePet.Plugins.NapCat;

namespace SoufflePet.Plugins.Tests;

/// <summary>
/// NapCat 消息解析：**装饰是数据不是文本**。
///
/// 正文一旦被拼上「@你」「引用消息」，宿主就再也认不出命令——群聊里 @桌宠 发 /model
/// 曾经因为正文变成「@你 /model …」而永远进不了命令分支（私聊没有 @ 段，所以看起来只有群聊坏）。
/// 这些用例锁住三点：@ 自己只置标记、引用只记 id、别人的 @ 仍留在正文里。
/// </summary>
public sealed class NapCatMessageParserTests
{
    private const long SelfId = 10001;

    [Fact]
    public void AtSelfBecomesAFlagNotText()
    {
        using var document = JsonDocument.Parse("""
            {"message":[{"type":"at","data":{"qq":"10001"}},{"type":"text","data":{"text":" /model deepseek"}}]}
            """);

        var parsed = NapCatMessageParser.ParseMessage(document.RootElement, SelfId);

        Assert.True(parsed.MentionsSelf);
        // 正文干净：宿主据此认得出 /model
        Assert.Equal("/model deepseek", parsed.Text);
        Assert.Null(parsed.QuotedId);
    }

    [Fact]
    public void CqCodeAtSelfIsAlsoJustAFlag()
    {
        using var document = JsonDocument.Parse("""
            {"message_format":"string","message":"[CQ:at,qq=10001] /stop"}
            """);

        var parsed = NapCatMessageParser.ParseMessage(document.RootElement, SelfId);

        Assert.True(parsed.MentionsSelf);
        Assert.Equal("/stop", parsed.Text);
    }

    [Fact]
    public void ReplyKeepsQuotedIdInsteadOfPlaceholderText()
    {
        using var document = JsonDocument.Parse("""
            {"message":[
              {"type":"reply","data":{"id":"42"}},
              {"type":"at","data":{"qq":"10001"}},
              {"type":"text","data":{"text":" /model"}}
            ]}
            """);

        var parsed = NapCatMessageParser.ParseMessage(document.RootElement, SelfId);

        Assert.Equal("42", parsed.QuotedId);
        Assert.True(parsed.MentionsSelf);
        // 引用不再往正文里塞「引用消息」占位
        Assert.Equal("/model", parsed.Text);
        Assert.DoesNotContain("引用", parsed.Text);
    }

    [Fact]
    public void CqCodeReplyKeepsQuotedIdInsteadOfPlaceholderText()
    {
        using var document = JsonDocument.Parse("""
            {"message_format":"string","message":"[CQ:reply,id=42][CQ:at,qq=10001] /help"}
            """);

        var parsed = NapCatMessageParser.ParseMessage(document.RootElement, SelfId);

        Assert.Equal("42", parsed.QuotedId);
        Assert.True(parsed.MentionsSelf);
        Assert.Equal("/help", parsed.Text);
    }

    [Fact]
    public void AtOthersStaysInText()
    {
        using var document = JsonDocument.Parse("""
            {"message":[{"type":"at","data":{"qq":"all"}},{"type":"text","data":{"text":"开会"}}]}
            """);

        var parsed = NapCatMessageParser.ParseMessage(document.RootElement, SelfId);

        Assert.False(parsed.MentionsSelf);
        Assert.Equal("@全体成员开会", parsed.Text);

        using var other = JsonDocument.Parse("""
            {"message":[{"type":"at","data":{"qq":"10002"}},{"type":"text","data":{"text":" 看一下"}}]}
            """);

        var otherParsed = NapCatMessageParser.ParseMessage(other.RootElement, SelfId);

        // 没有对应结构化字段的 @ 仍然是正文的一部分（行首是他人的 @ 时整条消息按普通文本处理，命令不生效）
        Assert.False(otherParsed.MentionsSelf);
        Assert.Equal("@10002 看一下", otherParsed.Text);
    }

    [Fact]
    public void AtOthersUsesResolvedNickname()
    {
        using var document = JsonDocument.Parse("""
            {"message":[{"type":"at","data":{"qq":"10002"}},{"type":"text","data":{"text":" 看一下"}}]}
            """);

        var parsed = NapCatMessageParser.ParseMessage(
            document.RootElement,
            SelfId,
            resolveAtName: qq => qq == 10002 ? "张三" : null);

        Assert.Equal("@张三 看一下", parsed.Text);
    }

    [Fact]
    public void AtOthersFallsBackToQqWhenNicknameUnknown()
    {
        using var document = JsonDocument.Parse("""
            {"message":[{"type":"at","data":{"qq":"10002"}},{"type":"text","data":{"text":" 看一下"}}]}
            """);

        var parsed = NapCatMessageParser.ParseMessage(
            document.RootElement,
            SelfId,
            resolveAtName: _ => null);

        Assert.Equal("@10002 看一下", parsed.Text);
    }

    [Fact]
    public void CqCodeAtOthersUsesResolvedNickname()
    {
        using var document = JsonDocument.Parse("""
            {"message_format":"string","message":"[CQ:at,qq=10002] 看一下"}
            """);

        var parsed = NapCatMessageParser.ParseMessage(
            document.RootElement,
            SelfId,
            resolveAtName: qq => qq == 10002 ? "张三" : null);

        Assert.Equal("@张三 看一下", parsed.Text);
    }

    [Fact]
    public void MentionOnlyMessageHasEmptyText()
    {
        using var document = JsonDocument.Parse("""
            {"message":[{"type":"at","data":{"qq":"10001"}}]}
            """);

        var parsed = NapCatMessageParser.ParseMessage(document.RootElement, SelfId);

        // 正文可以为空：插件据此仍会唤醒宿主（只 @ 一下也是消息），
        // 但空正文绝不带「@你」前缀
        Assert.True(parsed.MentionsSelf);
        Assert.Equal(string.Empty, parsed.Text);
    }
}
