// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SoufflePet.Chat;
using SoufflePet.Core.Chat;
using SoufflePet.Models;
using SoufflePet.Plugin.Sdk;
using SoufflePet.Services;
using Xunit;

namespace SoufflePet.Core.Tests;

/// <summary>
/// 平台消息里的命令：管理员判定（由平台插件按自己的设置决定）、命令白名单、
/// 平台回执与普通消息兼容。这些用例覆盖 ChatTriggerRouter 的平台分支——此前没有任何测试，
/// 导致「插件给正文加前缀 → 命令分支永远不命中」这类问题长期不可见。
/// </summary>
public sealed class PlatformCommandTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "soufflepet-platform-tests", Guid.NewGuid().ToString("N"));

    private SouffleSettings _settings = null!;
    private ChatSessionManager _manager = null!;
    private PlatformService _platforms = null!;
    private FakePlatform _napcat = null!;
    private ChatTriggerRouter _router = null!;
    private readonly FakeLlm _llm = new();

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _settings = new SouffleSettings { ChatMemoryEnabled = false, TimeAwarenessEnabled = false };
        _settings.ChatProviders.Add(new ChatProviderConfig
        {
            Name = "deepseek",
            BaseUrl = "https://example.test/v1",
            ApiKey = "k",
            Models =
            [
                new ChatProviderModel { ModelId = "deepseek-chat", Name = "DeepSeek Chat", IsActive = true },
                new ChatProviderModel { ModelId = "deepseek-reasoner", Name = "DeepSeek Reasoner", IsActive = true },
            ],
        });
        _settings.RebuildChatModels();
        _settings.ChatModelKey = _settings.ChatModels[0].Key;

        var store = new ChatSessionStore(Path.Combine(_root, "sessions"), Path.Combine(_root, "memory.json"));
        var factory = new ChatSessionFactory(
            _settings,
            _llm,
            new ChatToolAccess(_settings),
            new UserQuestionService(),
            new ChatFileService(),
            new FakeStatistics());
        _manager = new ChatSessionManager(_settings, factory, store);

        _platforms = new PlatformService();
        _napcat = new FakePlatform("napcat");
        _platforms.Register(_napcat);
        _router = new ChatTriggerRouter(_manager, _platforms, new FakeScheduler());
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _router.Dispose();
        _platforms.Dispose();
        await _manager.DisposeAsync();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 测试目录偶尔被索引器占用：让系统自己清理
        }
    }

    [Fact]
    public async Task AdminCommandSwitchesModelAndRepliesToPlatform()
    {
        _napcat.Admins.Add("10001");

        _napcat.Push("/model DeepSeek Reasoner", senderId: "10001");

        var session = await WaitForInboxAsync(messages => messages.Any(m => m.Text.Contains("已切换模型")));
        Assert.Equal(_settings.ChatModels[1].Key, session.Snapshot.SelectedModelKey);

        var receipt = await WaitForReceiptAsync();
        Assert.Equal("group:1", receipt.ConversationId);
        Assert.Contains("已切换模型", receipt.Text);

        // 命令不进模型上下文：平台消息会话里只有通知气泡
        Assert.DoesNotContain(session.Snapshot.Messages, message => message.Kind == ChatMessageKinds.Assistant);
    }

    [Fact]
    public async Task CommandFromNonAdminIsIgnoredAndNotForwardedToModel()
    {
        _napcat.Admins.Add("10001");

        _napcat.Push("/model DeepSeek Reasoner", senderId: "20002");

        var session = await WaitForInboxAsync(messages => messages.Any(m => m.Text.Contains("命令管理员")));
        Assert.Equal(_settings.ChatModels[0].Key, session.Snapshot.SelectedModelKey);
        Assert.Empty(_napcat.Sent);
    }

    [Fact]
    public async Task CommandWithoutSenderIdIsRejected()
    {
        _napcat.Admins.Add("10001");

        _napcat.Push("/stop", senderId: null);

        var session = await WaitForInboxAsync(messages => messages.Any(m => m.Text.Contains("命令管理员")));
        Assert.Equal(_settings.ChatModels[0].Key, session.Snapshot.SelectedModelKey);
        Assert.Empty(_napcat.Sent);
    }

    [Fact]
    public async Task PlatformWithoutAdminSupportIsRejectedByDefault()
    {
        // 平台没有实现 AllowsPlatformCommands → 用 SDK 的接口默认实现（一律拒绝）
        var legacy = new LegacyPlatform("legacy");
        _platforms.Register(legacy);

        legacy.Push("/model DeepSeek Reasoner", senderId: "10001");

        var session = await WaitForInboxAsync(messages => messages.Any(m => m.Text.Contains("命令管理员")));
        Assert.Equal(_settings.ChatModels[0].Key, session.Snapshot.SelectedModelKey);
        Assert.Empty(legacy.Sent);
    }

    [Fact]
    public async Task ApprovalCommandIsDeniedOnPlatformEvenForAdmin()
    {
        _napcat.Admins.Add("10001");

        _napcat.Push("/approval full", senderId: "10001");

        var receipt = await WaitForReceiptAsync();
        Assert.Contains("桌面端", receipt.Text);
        Assert.Equal(ToolApprovalMode.Manual, _settings.ToolApproval);

        var session = await WaitForInboxAsync(messages => messages.Any(m => m.Text.Contains("桌面端")));
        Assert.Equal(ChatApprovalModes.Manual, session.Snapshot.ApprovalMode);
    }

    [Fact]
    public async Task PlainPlatformTextStillGoesToTheModel()
    {
        _napcat.Push("今天天气怎么样", senderId: "20002");

        var session = await WaitForInboxAsync(messages =>
            messages.Any(m => m.Kind == ChatMessageKinds.Notice && m.Text.EndsWith("：今天天气怎么样", StringComparison.Ordinal)));
        Assert.Empty(_napcat.Sent);
        Assert.Equal(_settings.ChatModels[0].Key, session.Snapshot.SelectedModelKey);
    }

    [Fact]
    public async Task UnknownSlashTextIsForwardedAsNormalMessage()
    {
        _napcat.Admins.Add("10001");

        _napcat.Push("/nowhere 参数", senderId: "10001");

        var session = await WaitForInboxAsync(messages =>
            messages.Any(m => m.Kind == ChatMessageKinds.Notice && m.Text.EndsWith("：/nowhere 参数", StringComparison.Ordinal)));
        Assert.Empty(_napcat.Sent);
    }

    /// <summary>
    /// 群里 @ 桌宠 再发命令：@ 与引用都是结构化字段，正文仍然以 / 开头，命令分支照常命中。
    /// 这正是 NapCat 群聊的实际形态（此前插件把「@你」拼进正文，命令永远不命中）。
    /// </summary>
    [Fact]
    public async Task MentionAndQuoteDoNotBreakGroupCommand()
    {
        _napcat.Admins.Add("10001");

        _napcat.Push("/model DeepSeek Reasoner", senderId: "10001", mentionsSelf: true, quote: "小红：帮我看看");

        var receipt = await WaitForReceiptAsync();
        Assert.Contains("已切换模型", receipt.Text);
        Assert.Equal(_settings.ChatModels[1].Key, _settings.ChatModelKey);
        Assert.Empty(_llm.Prompts);
    }

    [Fact]
    public async Task MentionAndQuoteTravelAsAttributesNotContent()
    {
        _napcat.Push("今天天气怎么样", senderId: "20002", mentionsSelf: true, quote: "小红：明天呢");

        var session = await WaitForInboxAsync(messages => messages.Any(m => m.Text.Contains("今天天气怎么样")));
        var notice = Assert.Single(session.Snapshot.Messages, m => m.Text.Contains("今天天气怎么样"));
        // 展示行把标记拼在发送者后面，正文保持原样
        Assert.Contains("（@你 · 引用 小红：明天呢）：今天天气怎么样", notice.Text);

        var prompt = await WaitForPromptAsync(text => text.Contains("今天天气怎么样"));
        Assert.Contains("mentions_self=\"true\"", prompt);
        Assert.Contains("quote=\"小红：明天呢\"", prompt);
        // 正文里没有任何装饰：命令识别与模型理解都靠干净的正文 + 属性
        //（提示词按 AppendLine 拼，统一换成 \n 再断言）
        Assert.Contains("<content>\n今天天气怎么样\n</content>", prompt.Replace("\r\n", "\n"));
        Assert.DoesNotContain("@你", prompt);
    }

    [Fact]
    public async Task MentionWithoutBodyStillReachesTheModel()
    {
        // 群里只 @ 一下、不写字：正文为空，但消息仍然值得回应（此前被「正文为空」直接丢掉）
        _napcat.Push(string.Empty, senderId: "20002", mentionsSelf: true);

        var session = await WaitForInboxAsync(messages => messages.Any(m => m.Text.Contains("（无正文）")));
        var notice = Assert.Single(session.Snapshot.Messages, m => m.Text.Contains("（无正文）"));
        Assert.Contains("（@你）：（无正文）", notice.Text);

        var prompt = await WaitForPromptAsync(text => text.Contains("mentions_self=\"true\""));
        Assert.Contains("<content>", prompt);
        Assert.DoesNotContain("@你", prompt);
    }

    /// <summary>等「平台消息」会话里出现满足条件的消息，返回该会话（命令处理是后台异步的）。</summary>
    private async Task<ChatSession> WaitForInboxAsync(Func<IReadOnlyList<ChatMessageState>, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var session in _manager.OpenSessions)
            {
                if (predicate(session.Snapshot.Messages)) return session;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("等待平台消息会话超时");
    }

    private async Task<(string ConversationId, string Text)> WaitForReceiptAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (_napcat.Sent.Count > 0) return _napcat.Sent[0];
            await Task.Delay(20);
        }

        throw new TimeoutException("等待平台回执超时");
    }

    /// <summary>等模型收到包含指定内容的提示词，返回该提示词全文。</summary>
    private async Task<string> WaitForPromptAsync(Func<string, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var prompt in _llm.Prompts)
            {
                if (predicate(prompt)) return prompt;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("等待模型提示词超时");
    }

    /// <summary>可配置管理员的平台：AllowsPlatformCommands 按 Admins 集合判定，语义与内置插件一致。</summary>
    private class FakePlatform(string id) : RecordingPlatform(id), IMessagePlatform
    {
        public HashSet<string> Admins { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool AllowsPlatformCommands(string? senderId) =>
            senderId is { Length: > 0 } && Admins.Contains(senderId.Trim());
    }

    /// <summary>
    /// 只负责收发的平台基类：**不**实现 AllowsPlatformCommands，
    /// 继承它的类要么自己实现（<see cref="FakePlatform" />），要么落到接口默认的「一律拒绝」（<see cref="LegacyPlatform" />）。
    /// </summary>
    private abstract class RecordingPlatform(string id) : IMessagePlatform
    {
        private readonly List<(string ConversationId, string Text)> _sent = [];

        public string Id { get; } = id;

        public string? PlatformPrompt => null;

        public event EventHandler<PlatformIncomingMessage>? OnMentioned;

        public IReadOnlyList<(string ConversationId, string Text)> Sent
        {
            get { lock (_sent) return [.. _sent]; }
        }

        public Task<IReadOnlyList<PlatformConversation>> GetConversationsAsync() =>
            Task.FromResult<IReadOnlyList<PlatformConversation>>([new PlatformConversation("group:1", "测试群")]);

        public Task<string> GetMessagesAsync(string conversationId, int limit = 20)
            => Task.FromResult(string.Empty);

        public Task SendMessageAsync(string conversationId, string message)
        {
            lock (_sent) _sent.Add((conversationId, message));
            return Task.CompletedTask;
        }

        public Task SendAttachmentAsync(string conversationId, PlatformAttachment attachment)
            => Task.CompletedTask;

        /// <summary>模拟插件上报一条消息：正文已经是纯正文，会话 id 与发送者 id 单独给。</summary>
        public void Push(string content, string? senderId, bool mentionsSelf = false, string? quote = null)
            => OnMentioned?.Invoke(this, new PlatformIncomingMessage(
                Id, content, "小明", Guid.NewGuid().ToString("N"), null, senderId, "group:1", mentionsSelf, quote));
    }

    /// <summary>没有实现 AllowsPlatformCommands 的平台：走 SDK 接口的默认实现（一律拒绝）。</summary>
    private sealed class LegacyPlatform(string id) : RecordingPlatform(id);

    private sealed class FakeLlm : ILlmChatService
    {
        private readonly List<string> _prompts = [];

        /// <summary>每次调用的完整历史（拼成一整段文本），用来断言系统提示词与平台消息标记。</summary>
        public IReadOnlyList<string> Prompts
        {
            get { lock (_prompts) return [.. _prompts]; }
        }

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            ChatModelConfig model,
            IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> history,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            lock (_prompts) _prompts.Add(string.Join("\n", history.Select(message => message.Text)));
            await Task.Yield();
            yield return new LlmTextDelta("ok");
        }
    }

    private sealed class FakeStatistics : IStatisticsService
    {
        public string UsageText => string.Empty;

        public UsageSnapshot Usage => default;

        public GlobalUsageSnapshot GlobalUsage => default;

        public void AddUsage(long inputTokens, long outputTokens, long cachedInputTokens)
        {
        }

        public void AddPoke()
        {
        }

        public void AddHeadPat()
        {
        }

        public void AddConversation()
        {
        }

        public IReadOnlyList<StatisticItem> Snapshot() => [];
    }

    private sealed class FakeScheduler : IScheduledTaskService
    {
        public event EventHandler<ScheduledTaskTrigger>? Triggered
        {
            add { }
            remove { }
        }

        public event EventHandler? LogsChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<ScheduledTaskLogEntry> Logs => [];

        public void Start()
        {
        }

        public void Refresh()
        {
        }

        public void RunNow(ScheduledTask task)
        {
        }

        public void ClearLogs()
        {
        }

        public IReadOnlyList<ScheduledTask> ListScheduledTasks() => [];

        public ScheduledTask? FindTask(string id) => null;

        public ScheduledTask CreateScheduledTask(
            string name,
            string content,
            ScheduledTaskSchedule schedule,
            string? workspacePath = null,
            string? modelKey = null) => throw new NotSupportedException();

        public ScheduledTask? UpdateScheduledTask(
            string id,
            string? name,
            string? content,
            ScheduledTaskSchedule? schedule,
            bool? enabled,
            string? workspacePath = null,
            string? modelKey = null) => null;

        public ScheduledTask? DeleteScheduledTask(string id) => null;

        public void Dispose()
        {
        }
    }
}

/// <summary>主机侧的平台命令判定：平台不在线、插件抛异常、发送者 id 缺失都按拒绝处理。</summary>
public sealed class PlatformCommandAuthorizationTests
{
    [Fact]
    public void UnknownPlatformIsDenied()
    {
        using var platforms = new PlatformService();
        Assert.False(platforms.AllowsPlatformCommands("napcat", "10001"));
        Assert.False(platforms.AllowsPlatformCommands(null, "10001"));
        Assert.False(platforms.AllowsPlatformCommands("", "10001"));
    }

    [Fact]
    public void PlatformWithoutImplementationDeniesEveryone()
    {
        using var platforms = new PlatformService();
        // 没有实现 AllowsPlatformCommands：走 SDK 的接口默认实现（false）
        platforms.Register(new PlainPlatform("plain"));

        Assert.False(platforms.AllowsPlatformCommands("plain", "alice"));
    }

    [Fact]
    public void PlatformDecisionIsUsedAndMissingSenderIsDenied()
    {
        using var platforms = new PlatformService();
        platforms.Register(new AllowingPlatform("demo", allow: "alice"));

        Assert.True(platforms.AllowsPlatformCommands("demo", "alice"));
        Assert.True(platforms.AllowsPlatformCommands(" DEMO ", "alice"));
        Assert.False(platforms.AllowsPlatformCommands("demo", "bob"));
        Assert.False(platforms.AllowsPlatformCommands("demo", null));
        Assert.False(platforms.AllowsPlatformCommands("demo", "  "));
    }

    [Fact]
    public void ThrowingPlatformIsDenied()
    {
        using var platforms = new PlatformService();
        platforms.Register(new ThrowingPlatform("boom"));

        Assert.False(platforms.AllowsPlatformCommands("boom", "alice"));
    }

    private sealed class PlainPlatform(string id) : StubPlatform(id);

    private sealed class AllowingPlatform(string id, string allow) : StubPlatform(id), IMessagePlatform
    {
        public bool AllowsPlatformCommands(string? senderId) =>
            string.Equals(senderId?.Trim(), allow, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ThrowingPlatform(string id) : StubPlatform(id), IMessagePlatform
    {
        public bool AllowsPlatformCommands(string? senderId) =>
            throw new InvalidOperationException("配置读取失败");
    }

    /// <summary>只实现 IMessagePlatform 的必要成员；不实现 AllowsPlatformCommands，让子类决定。</summary>
    private abstract class StubPlatform(string id) : IMessagePlatform
    {
        public string Id { get; } = id;

        public string? PlatformPrompt => null;

        public event EventHandler<PlatformIncomingMessage>? OnMentioned
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<PlatformConversation>> GetConversationsAsync() =>
            Task.FromResult<IReadOnlyList<PlatformConversation>>([]);

        public Task<string> GetMessagesAsync(string conversationId, int limit = 20) =>
            Task.FromResult(string.Empty);

        public Task SendMessageAsync(string conversationId, string message) => Task.CompletedTask;

        public Task SendAttachmentAsync(string conversationId, PlatformAttachment attachment) =>
            Task.CompletedTask;
    }
}
