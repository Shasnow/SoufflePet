// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SoufflePet.Core.Chat;
using SoufflePet.Models;
using SoufflePet.Services;
using Xunit;

namespace SoufflePet.Core.Tests;

/// <summary>ChatSession / ChatSessionManager 的行为测试：假模型流、审批、提问、取消、持久化与迁移。</summary>
public sealed class ChatSessionTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "soufflepet-chat-tests", Guid.NewGuid().ToString("N"));

    private SouffleSettings _settings = null!;
    private ChatToolAccess _access = null!;
    private UserQuestionService _questions = null!;
    private ChatFileService _files = null!;
    private FakeStatistics _statistics = null!;
    private ChatSessionStore _store = null!;
    private ChatSessionFactory _factory = null!;
    private ChatSessionManager _manager = null!;

    private string SessionsRoot => Path.Combine(_root, "sessions");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _settings = new SouffleSettings { ChatMemoryEnabled = true, TimeAwarenessEnabled = false };
        _settings.ChatProviders.Add(new ChatProviderConfig
        {
            Name = "p",
            BaseUrl = "https://example.test/v1",
            ApiKey = "k",
            Models = [new ChatProviderModel { ModelId = "model", Name = "模型", IsActive = true }],
        });
        _settings.RebuildChatModels();
        _settings.ChatModelKey = _settings.ChatModels[0].Key;

        _access = new ChatToolAccess(_settings);
        _questions = new UserQuestionService();
        _files = new ChatFileService();
        _statistics = new FakeStatistics();
        _store = new ChatSessionStore(_root,
            Path.Combine(_root, "profile.json"),
            Path.Combine(_root, "conversations.jsonl"));

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 测试目录偶尔被索引器占用：让系统自己清理
        }

        return Task.CompletedTask;
    }

    private ChatSessionManager CreateManager(FakeLlm llm)
    {
        _factory = new ChatSessionFactory(_settings, llm, _access, _questions, _files, _statistics);
        _manager = new ChatSessionManager(_settings, _factory, _store);
        return _manager;
    }

    [Fact]
    public async Task SendStreamsTextAndReloadsFromSessionFile()
    {
        var llm = new FakeLlm((_, _, _) => Streams(
            new LlmTextDelta("你"),
            new LlmTextDelta("好"),
            new LlmUsageEvent(11, 3, 2)));
        var manager = CreateManager(llm);
        var session = manager.ActiveSession!;

        await session.SendAsync("在吗", null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var snapshot = session.Snapshot;
        Assert.False(snapshot.Generating);
        Assert.Collection(snapshot.Messages,
            message =>
            {
                Assert.Equal(ChatMessageKinds.User, message.Kind);
                Assert.Equal("在吗", message.Text);
            },
            message =>
            {
                Assert.Equal(ChatMessageKinds.Assistant, message.Kind);
                Assert.Equal("你好", message.Text);
                Assert.Equal(11, message.Usage!.InputTokens);
            });
        Assert.Equal(1, _statistics.Conversations);

        var id = session.Id;
        await manager.DisposeAsync();
        _manager = null!;

        var reloaded = new ChatSessionManager(_settings, _factory, _store);
        try
        {
            var opened = await reloaded.OpenAsync(id, CancellationToken.None);
            var restored = opened.Snapshot;
            Assert.Equal(2, restored.Messages.Count);
            Assert.Equal("你好", restored.Messages[1].Text);
            Assert.Equal(11, restored.Messages[1].Usage!.InputTokens);
            Assert.Equal(11, restored.Usage!.InputTokens);
            Assert.Equal(2, restored.Usage.CachedInputTokens);
        }
        finally
        {
            await reloaded.DisposeAsync();
        }
    }

    [Fact]
    public async Task ApprovalDecisionAllowsToolToContinue()
    {
        var llm = new FakeLlm((_, _, ct) => ApprovalStreamAsync(ct));
        var manager = CreateManager(llm);
        var session = manager.ActiveSession!;

        async IAsyncEnumerable<LlmStreamEvent> ApprovalStreamAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var approved = await _access.RequestApprovalAsync("run_shell_command", "echo hi", ct);
            await Task.Yield();
            yield return new LlmTextDelta(approved ? "已执行" : "已拒绝");
        }

        await session.SendAsync("跑个命令", null, CancellationToken.None);
        var approval = await WaitForAsync(() =>
            session.Snapshot.Messages.FirstOrDefault(message => message.Kind == ChatMessageKinds.Approval));
        Assert.True(approval.Approval!.Pending);

        await session.DecideApprovalAsync(approval.Id, approve: true);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var final = session.Snapshot.Messages.First(message => message.Id == approval.Id);
        Assert.False(final.Approval!.Pending);
        Assert.Equal("approved", final.Approval.Outcome);
        Assert.Equal("已执行", session.Snapshot.Messages.Last().Text);
    }

    [Fact]
    public async Task AskSubmitResolvesTool()
    {
        var llm = new FakeLlm((_, _, ct) => AskStreamAsync(ct));
        var manager = CreateManager(llm);
        var session = manager.ActiveSession!;

        async IAsyncEnumerable<LlmStreamEvent> AskStreamAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var answers = await _questions.AskAsync(
                [new UserQuestion("选哪个", [new UserOption("A"), new UserOption("B")])], ct);
            await Task.Yield();
            yield return new LlmTextDelta(answers is { Count: > 0 } ? answers[0].Answer : "未回答");
        }

        await session.SendAsync("问个问题", null, CancellationToken.None);
        var ask = await WaitForAsync(() => session.Snapshot.Ask);
        Assert.Equal(2, ask.Questions[0].Options.Count);

        var payload = JsonSerializer.SerializeToElement(new
        {
            answers = new[] { new { question = ask.Questions[0].Text, answer = "B" } },
        });
        await session.SubmitAskAsync(ask.RequestId, "submit", payload);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(session.Snapshot.Ask);
        Assert.Equal("B", session.Snapshot.Messages.Last().Text);
    }

    [Fact]
    public async Task StopCancelsTurnAndKeepsPartialText()
    {
        var llm = new FakeLlm((_, _, ct) => SlowStreamAsync(ct));
        var manager = CreateManager(llm);
        var session = manager.ActiveSession!;

        async IAsyncEnumerable<LlmStreamEvent> SlowStreamAsync([EnumeratorCancellation] CancellationToken ct)
        {
            yield return new LlmTextDelta("一半");
            await Task.Delay(Timeout.Infinite, ct);
            yield return new LlmTextDelta("不会到达");
        }

        await session.SendAsync("慢慢说", null, CancellationToken.None);
        await WaitForAsync(() =>
            session.Snapshot.Messages.LastOrDefault(message => message.Text == "一半"));
        await session.StopAsync();
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(session.Snapshot.Generating);
        Assert.Equal("一半", session.Snapshot.Messages.Last().Text);
    }

    [Fact]
    public async Task ToolEventsProduceToolBubble()
    {
        var llm = new FakeLlm((_, _, _) => Streams(
            new LlmToolCallStartedEvent("call-1", "read_file", "{\"path\":\"a.txt\"}"),
            new LlmToolCallEvent("call-1", "read_file", "{\"path\":\"a.txt\"}", "内容"),
            new LlmTextDelta("读完了")));
        var manager = CreateManager(llm);
        var session = manager.ActiveSession!;

        await session.SendAsync("读文件", null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var messages = session.Snapshot.Messages;
        var tool = messages.Single(message => message.Kind == ChatMessageKinds.Tool);
        Assert.Equal("read_file", tool.Tool!.Name);
        Assert.Contains("内容", tool.Tool.Result);
        Assert.Equal("读完了", messages.Last().Text);
    }

    [Fact]
    public async Task ManagerCreatesRenamesSearchesAndDeletesWithRecycleBin()
    {
        var legacy = Path.Combine(_root, "conversations.jsonl");
        await File.WriteAllLinesAsync(legacy,
        [
            JsonSerializer.Serialize(new { Time = DateTimeOffset.Now, Role = "user", Text = "旧会话第一条" }),
            JsonSerializer.Serialize(new { Time = DateTimeOffset.Now, Role = "assistant", Text = "旧回复" }),
        ]);

        var manager = CreateManager(new FakeLlm((_, _, _) => Streams(new LlmTextDelta("ok"))));
        var migrated = Assert.Single(manager.Sessions);
        Assert.Equal("旧会话第一条", migrated.Title);
        Assert.True(File.Exists(legacy + ".bak"));

        var created = await manager.CreateAsync(null, CancellationToken.None);
        await manager.RenameAsync(created.Id, "改过的标题", CancellationToken.None);
        var search = await manager.SearchAsync("改过", CancellationToken.None);
        Assert.Contains(search, record => record.Id == created.Id);

        var payload = await manager.DeleteAsync(created.Id, CancellationToken.None);
        Assert.DoesNotContain(payload.Sessions, record => record.Id == created.Id);
        Assert.Equal(migrated.Id, payload.ActiveId);
        Assert.True(Directory.Exists(Path.Combine(SessionsRoot, ".deleted")));

        var opened = await manager.OpenAsync(migrated.Id, CancellationToken.None);
        Assert.Equal(2, opened.Snapshot.Messages.Count);
        Assert.True(File.Exists(legacy));
    }

    [Fact]
    public async Task ProfileIsSharedAcrossSessionMemories()
    {
        var profile = Path.Combine(_root, "profile.json");
        var a = new ChatMemory(_settings, Path.Combine(_root, "a"), "conversation.jsonl", profile);
        var b = new ChatMemory(_settings, Path.Combine(_root, "b"), "conversation.jsonl", profile);

        a.Remember("城市", "杭州");
        b.Remember("宠物", "猫");

        var c = new ChatMemory(_settings, Path.Combine(_root, "c"), "conversation.jsonl", profile);
        var builder = new StringBuilder();
        c.BuildProfileBlock(builder);
        Assert.Contains("城市", builder.ToString());
        Assert.Contains("宠物", builder.ToString());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ApprovalIsRoutedToCurrentSessionWithMultipleLoaded()
    {
        var llm = new FakeLlm((_, _, ct) => ApprovalStreamAsync(ct));
        var manager = CreateManager(llm);
        var first = manager.ActiveSession!;

        async IAsyncEnumerable<LlmStreamEvent> ApprovalStreamAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var approved = await _access.RequestApprovalAsync("run_shell_command", "echo two", ct);
            await Task.Yield();
            yield return new LlmTextDelta(approved ? "第二会话允许" : "第二会话拒绝");
        }

        // 第一个会话已加载并订阅了审批事件；审批必须仍落到真正在生成的新会话
        var second = await manager.CreateAsync(null, CancellationToken.None);
        var active = manager.ActiveSession!;
        Assert.NotEqual(first.Id, active.Id);

        await active.SendAsync("在第二个会话跑命令", null, CancellationToken.None);
        var approval = await WaitForAsync(() =>
            active.Snapshot.Messages.FirstOrDefault(message => message.Kind == ChatMessageKinds.Approval));
        await active.DecideApprovalAsync(approval.Id, approve: true);
        await active.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("第二会话允许", active.Snapshot.Messages.Last().Text);
        Assert.DoesNotContain(first.Snapshot.Messages, message => message.Kind == ChatMessageKinds.Approval);
        Assert.Equal(second.Id, active.Id);
    }

    [Fact]
    public async Task LocalAttachmentsAreConsumedAndPassedToModelAsPaths()
    {
        var attachmentPath = Path.Combine(_root, "note.txt");
        await File.WriteAllTextAsync(attachmentPath, "附件内容");

        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>? captured = null;
        var llm = new FakeLlm((_, history, _) =>
        {
            captured = history;
            return Streams(new LlmTextDelta("收到附件"));
        });
        var manager = CreateManager(llm);
        var session = manager.ActiveSession!;

        var attachments = await session.AddAttachmentsAsync([attachmentPath]);
        Assert.Single(attachments);
        Assert.True(session.Snapshot.Attachments.Count == 1);

        await session.SendAsync("看看这个", [attachments[0].Id], CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var messages = session.Snapshot.Messages;
        Assert.Contains("[附件] note.txt", messages[0].Text);
        Assert.Empty(session.Snapshot.Attachments);
        Assert.NotNull(captured);
        var request = captured!.Last(message => message.Role == Microsoft.Extensions.AI.ChatRole.User);
        Assert.Contains($"@{attachmentPath}", request.Text);
    }

    [Fact]
    public async Task DeletingActiveSessionSwitchesToRemainingThenCreatesReplacement()
    {
        var manager = CreateManager(new FakeLlm((_, _, _) => Streams(new LlmTextDelta("ok"))));
        var first = Assert.Single(manager.Sessions);
        var second = await manager.CreateAsync(null, CancellationToken.None);
        Assert.Equal(second.Id, manager.ActiveSessionId);

        var afterSecond = await manager.DeleteAsync(second.Id, CancellationToken.None);
        Assert.Equal(first.Id, afterSecond.ActiveId);
        Assert.Single(afterSecond.Sessions);

        var afterFirst = await manager.DeleteAsync(first.Id, CancellationToken.None);
        Assert.NotNull(afterFirst.ActiveId);
        Assert.NotEqual(first.Id, afterFirst.ActiveId);
        Assert.Single(afterFirst.Sessions);
        Assert.NotNull(manager.ActiveSession);
        Assert.True(Directory.Exists(Path.Combine(SessionsRoot, ".deleted")));
    }

    [Fact]
    public async Task RefreshModelsDropsSelectionWhenProviderRemoved()
    {
        var manager = CreateManager(new FakeLlm((_, _, _) => Streams(new LlmTextDelta("ok"))));
        var session = manager.ActiveSession!;
        await session.SelectModelAsync(_settings.ChatModels[0].Key);
        Assert.Single(session.Snapshot.Models);

        _settings.ChatProviders.Clear();
        _settings.RebuildChatModels();
        session.RefreshModels();

        Assert.Empty(session.Snapshot.Models);
        Assert.Null(session.Snapshot.SelectedModelKey);
    }

    [Fact]
    public void OpsSerializeWithContractDiscriminator()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize<ChatOp>(
            new ChatOpMessagePatchOp("m1", new ChatMessagePatch(Expanded: true)), options);
        Assert.Contains("\"op\":\"msg:patch\"", json);
        Assert.Contains("\"expanded\":true", json);
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> probe, int timeoutMs = 5000) where T : class
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < TimeSpan.FromMilliseconds(timeoutMs))
        {
            if (probe() is { } value) return value;
            await Task.Delay(20);
        }

        throw new TimeoutException("等待会话状态超时");
    }

    private static async IAsyncEnumerable<LlmStreamEvent> Streams(params LlmStreamEvent[] events)
    {
        foreach (var evt in events)
        {
            await Task.Yield();
            yield return evt;
        }
    }

    private sealed class FakeLlm(
        Func<ChatModelConfig, IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>, CancellationToken,
            IAsyncEnumerable<LlmStreamEvent>> stream) : ILlmChatService
    {
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            ChatModelConfig model,
            IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> history,
            CancellationToken cancellationToken) => stream(model, history, cancellationToken);
    }

    private sealed class FakeStatistics : IStatisticsService
    {
        public int Conversations { get; private set; }

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

        public void AddConversation() => Conversations++;

        public IReadOnlyList<StatisticItem> Snapshot() => [];
    }
}