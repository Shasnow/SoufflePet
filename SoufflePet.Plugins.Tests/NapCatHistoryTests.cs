// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SoufflePet.Plugins.NapCat;

namespace SoufflePet.Plugins.Tests;

/// <summary>
/// NapCat 会话历史：装饰（@ 了你 / 引用）存成字段，正文永远是纯正文，
/// 「[会话 id] 发送者（标记）：正文」只在读取时拼出来。
/// 这样历史行、唤醒上下文与上报给宿主的正文用的是同一份事实，不会出现「正文里带标记 → 命令识别不到」。
/// </summary>
public sealed class NapCatHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "soufflepet-napcat-history", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 目录偶尔被索引器占用：让系统自己清理
        }
    }

    [Fact]
    public void HistoryKeepsDecorationsAsFields()
    {
        var store = new NapCatConversationStore(NullLogger.Instance, _root);

        store.RecordMessage(
            "group:1", "42", "/model deepseek", 10001, "小明", "群 1",
            mentionsSelf: true, quotedId: "41");
        store.PatchQuote("group:1", "42", "小红：明天呢");

        var message = Assert.Single(store.GetMessagesSinceLastReply("group:1", 5, selfId: 0));
        // 记录本身只有事实字段，正文不带任何装饰
        Assert.Equal("/model deepseek", message.Text);
        Assert.True(message.MentionsSelf);
        Assert.Equal("41", message.QuotedId);
        Assert.Equal("小红：明天呢", message.Quote);

        // 读历史时才拼成展示行
        var line = Assert.Single(store.GetRecentLines("group:1", 5));
        Assert.Equal("小明（@你 · 引用 小红：明天呢）：/model deepseek", line);
        // 行内不重复会话 id：唤醒上下文与 get_platform_messages 的头部已经给出
        Assert.DoesNotContain("group:1", line);
    }

    [Fact]
    public void DecorationsSurviveCacheRoundTrip()
    {
        var store = new NapCatConversationStore(NullLogger.Instance, _root);
        store.RecordMessage(
            "group:1", "42", "看这句", 10001, "小明", "群 1",
            mentionsSelf: true, quotedId: "41");
        store.PatchQuote("group:1", "42", "小红：明天呢");
        // 只 @ 一下、没有正文的消息也要能存下来
        store.RecordMessage("group:1", "43", string.Empty, 10001, "小明", null, mentionsSelf: true);
        store.SaveIfDirty();

        var reloaded = new NapCatConversationStore(NullLogger.Instance, _root);
        reloaded.Load();

        var messages = reloaded.GetMessagesSinceLastReply("group:1", 5, selfId: 0);
        Assert.Equal(2, messages.Count);
        Assert.True(messages[0].MentionsSelf);
        Assert.Equal("41", messages[0].QuotedId);
        Assert.Equal("小红：明天呢", messages[0].Quote);
        Assert.Equal("看这句", messages[0].Text);
        Assert.True(messages[1].MentionsSelf);
        Assert.Equal(string.Empty, messages[1].Text);
    }

    [Theory]
    [InlineData("在吗", false, null, false, "小明：在吗")]
    [InlineData("在吗", true, null, false, "小明（@你）：在吗")]
    [InlineData("明天呢", false, "小红：明天呢", true, "小明（引用 小红：明天呢）：明天呢")]
    [InlineData("看这句", true, "小红：明天呢", true, "小明（@你 · 引用 小红：明天呢）：看这句")]
    // 引用反查失败：只知道「这是条回复」，摘要没有
    [InlineData("看这句", false, null, true, "小明（引用）：看这句")]
    // 只有 @ 没有正文
    [InlineData("", true, null, false, "小明（@你）：（无正文）")]
    public void FormatLineRendersStructuredMarks(
        string text,
        bool mentionsSelf,
        string? quote,
        bool quoted,
        string expected)
    {
        var message = new NapCatStoredMessage(
            "42", text, 10001, "小明", DateTimeOffset.UnixEpoch, mentionsSelf, quoted ? "41" : null, quote);

        Assert.Equal(expected, message.FormatLine());
    }

    [Fact]
    public void StoredMessageSerializesDecorationFields()
    {
        var message = new NapCatStoredMessage(
            "42", "看这句", 10001, "小明", DateTimeOffset.UnixEpoch, true, "41", "小红：明天呢");

        var json = JsonSerializer.Serialize(message);
        var restored = JsonSerializer.Deserialize<NapCatStoredMessage>(json);

        Assert.Equal(message, restored);
        Assert.Contains("MentionsSelf", json);
        Assert.Contains("QuotedId", json);
        Assert.Contains("Quote", json);
    }

    [Fact]
    public void WakeContextStartsAfterLastSelfReply()
    {
        const long selfId = 90001;
        var store = new NapCatConversationStore(NullLogger.Instance, _root);
        store.RecordMessage("group:1", "1", "早", 10001, "小明", "群 1");
        store.RecordSentMessage("group:1", "2", "早上好", "桌宠", selfId);
        store.RecordMessage("group:1", "3", "在吗", 10001, "小明", "群 1");
        store.RecordMessage("group:1", "4", "在吗？", 10002, "小红", "群 1");

        var messages = store.GetMessagesSinceLastReply("group:1", 10, selfId);

        // 上次回复之前的消息（包括桌宠自己的回复）不再作为上下文
        Assert.Equal(new[] { "3", "4" }, messages.Select(item => item.MessageId));
    }

    [Fact]
    public void WakeContextStopsBeforeCurrentMessage()
    {
        const long selfId = 90001;
        var store = new NapCatConversationStore(NullLogger.Instance, _root);
        store.RecordMessage("group:1", "1", "早", 10001, "小明", "群 1");
        store.RecordSentMessage("group:1", "2", "早上好", "桌宠", selfId);
        store.RecordMessage("group:1", "3", "在吗", 10001, "小明", "群 1");
        store.RecordMessage("group:1", "4", "补充一句", 10001, "小明", "群 1");
        // 异步补全期间才入队的更新消息：不属于当前这条的上下文
        store.RecordMessage("group:1", "5", "更晚的消息", 10002, "小红", "群 1");

        var messages = store.GetMessagesSinceLastReply("group:1", 10, selfId, currentMessageId: "4");

        Assert.Equal(new[] { "3" }, messages.Select(item => item.MessageId));
    }

    [Fact]
    public void WakeContextFallsBackToRecentMessagesBeforeFirstReply()
    {
        var store = new NapCatConversationStore(NullLogger.Instance, _root);
        store.RecordMessage("group:1", "1", "一", 10001, "小明", "群 1");
        store.RecordMessage("group:1", "2", "二", 10001, "小明", "群 1");
        store.RecordMessage("group:1", "3", "三", 10001, "小明", "群 1");

        // 还没回复过（selfId 未知也同理）：退回最近 maxCount 条，并按上限截断
        var messages = store.GetMessagesSinceLastReply("group:1", 2, selfId: 0);

        Assert.Equal(new[] { "2", "3" }, messages.Select(item => item.MessageId));
    }

    [Fact]
    public void AtTargetResolutionSkipsSelfMessages()
    {
        var store = new NapCatConversationStore(NullLogger.Instance, _root);
        store.RecordSentMessage("group:1", "1", "我是桌宠", "桌宠", 90001);
        store.RecordMessage("group:1", "2", "大家好", 10001, "小明", "群 1");

        // [at:桌宠] 不能解析成机器人自己，否则会 @ 到自己
        Assert.Equal(0, store.FindUserIdBySender("group:1", "桌宠", excludeUserId: 90001));
        Assert.Equal(10001, store.FindUserIdBySender("group:1", "小明", excludeUserId: 90001));
    }
}
