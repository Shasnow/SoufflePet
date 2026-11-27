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
using SoufflePet.Chat;
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
        _store = new ChatSessionStore(SessionsRoot, Path.Combine(_root, "memory.json"));

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
    public async Task AutoCompactsWhenContextExceedsWindow()
    {
        // 窗口 64K → 保留预算 32K token、触发线约 54.9K token；轮数与拆分点无关
        _settings.DefaultContextWindowTokens = 64 * 1024;

        var histories = new List<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>>();
        async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> history)
        {
            histories.Add(history);
            await Task.Yield();
            yield return new LlmTextDelta("回复");
        }

        var llm = new FakeLlm((_, history, _) => StreamAsync(history));
        var manager = CreateManager(llm);
        var session = manager.ActiveSession!;

        // 第一轮自己还在触发线以下（约 52.5K），第二轮补上后才超窗
        await session.SendAsync(new string('a', 210_000), null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await session.SendAsync(new string('b', 30_000), null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // 第二次请求前已压缩：摘要挂进历史，只保留预算内的最近一轮原文
        var last = histories[^1];
        Assert.Contains(last, message => message.Text.Contains("<conversation_summary>"));
        Assert.Equal(1, last.Count(message => message.Role == Microsoft.Extensions.AI.ChatRole.User));
        Assert.Contains(session.Snapshot.Messages, message => message.Text.Contains("已压缩上下文"));
        // 摘要本体单独一条气泡（默认展开），自动压缩也要能让用户看到被折叠成什么
        var summaryBubble = Assert.Single(
            session.Snapshot.Messages, message => message.Kind == ChatMessageKinds.Summary);
        Assert.Equal("已压缩的对话摘要", summaryBubble.Text);
        Assert.True(summaryBubble.Expanded);
    }

    [Fact]
    public async Task CompactionSplitsByTokensNotByTurnCount()
    {
        // 保留预算 32K token：历史总量（约 40K）超过预算但远没到自动触发线（54.9K），
        // 只要执行压缩，就必须由 token 预算决定拆分点（仅保留最后一轮），而不是「保留最近 30 轮」。
        _settings.DefaultContextWindowTokens = 64 * 1024;
        _settings.HistoryKeepTurns = 30;

        var summarized = new List<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>>();
        var llm = new FakeLlm(
            (_, _, _) => Streams(new LlmTextDelta("回复")),
            summary: "要点摘要",
            onSummarize: summarized.Add);
        var manager = CreateManager(llm);
        var session = manager.ActiveSession!;

        await session.SendAsync(new string('a', 160_000), null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await session.SendAsync("问题2", null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var result = await SlashCommands.ExecuteAsync(session, "/compact");

        Assert.True(result.Success);
        // 保留 1 轮原文并报告大致的保留 token 数
        Assert.Contains("保留最近 1 轮原文", result.Message);
        Assert.Single(summarized);

        var last = session.Snapshot.Messages.Last(message => message.Kind == ChatMessageKinds.Notice);
        Assert.Contains("已压缩上下文", last.Text);
    }

    [Fact]
    public async Task DropsOldestTurnsWhenCompactionFails()
    {
        _settings.DefaultContextWindowTokens = 64 * 1024;
        _settings.HistoryKeepTurns = 1;

        var histories = new List<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>>();
        async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> history)
        {
            histories.Add(history);
            await Task.Yield();
            yield return new LlmTextDelta("回复");
        }

        // 摘要恒为空 → 压缩失败，退回按轮数丢弃
        var llm = new FakeLlm((_, history, _) => StreamAsync(history), summary: string.Empty);
        var manager = CreateManager(llm);
        var session = manager.ActiveSession!;

        await session.SendAsync(new string('a', 210_000), null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await session.SendAsync(new string('b', 30_000), null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // 没有摘要，但旧的轮次已被丢弃：请求里只剩本轮提问
        var last = histories[^1];
        Assert.DoesNotContain(last, message => message.Text.Contains("<conversation_summary>"));
        Assert.Equal(1, last.Count(message => message.Role == Microsoft.Extensions.AI.ChatRole.User));
        Assert.Contains(session.Snapshot.Messages, message => message.Text.Contains("压缩失败"));
        Assert.Contains(session.Snapshot.Messages, message => message.Text.Contains("按轮数丢弃"));
        // 压缩没成，就没有摘要气泡（否则「摘要为空」也会让界面以为压缩成功）
        Assert.DoesNotContain(session.Snapshot.Messages, message => message.Kind == ChatMessageKinds.Summary);
    }

    [Fact]
    public async Task TurnContextGuardCompactsMidTurn()
    {
        // 窗口 64K → 触发线约 54.9K、保留预算 32.7K。第一轮 40K token 不会触发自动压缩，
        // 但已经超过保留预算，所以中途守卫压缩时必须丢掉它。
        _settings.DefaultContextWindowTokens = 64 * 1024;

        var estimates = 0;
        var histories = new List<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>>();
        var requestsSeen = new List<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>>();
        var guardResults = new List<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>?>();

        async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> history)
        {
            histories.Add(history);
            await Task.Yield();
            yield return new LlmTextDelta("回复");
        }

        // 入场检查报「还没超」（奇数次估算），工具循环中途报「已超」（偶数次估算），
        // 模拟工具结果把水位顶过触发线；守卫压缩后用压缩后的历史继续本轮请求。
        var llm = new FakeLlm(
            (_, history, _) => StreamAsync(history),
            estimate: (_, _) => Interlocked.Increment(ref estimates) % 2 == 1 ? 1_000 : 80_000,
            guard: async (contextGuard, request) =>
            {
                requestsSeen.Add(request);
                var compacted = await contextGuard.CompactIfNeededAsync(request, CancellationToken.None);
                guardResults.Add(compacted);
                return compacted;
            });
        var manager = CreateManager(llm);
        var session = manager.ActiveSession!;

        await session.SendAsync(new string('a', 160_000), null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await session.SendAsync("问题2", null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // 第一轮中途：只有一轮可摘要 → 不压缩；第二轮中途：压缩并返回重建后的历史
        Assert.Equal(2, guardResults.Count);
        Assert.Null(guardResults[0]);
        var compacted = Assert.IsAssignableFrom<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>>(guardResults[1]);
        Assert.Contains(compacted, message => message.Text.Contains("<conversation_summary>"));
        Assert.Contains(compacted, message => message.Text.Contains("问题2"));

        // 守卫拿到的是「即将发出的请求」：压缩前的那份，含本轮提问、不含系统提示词
        Assert.Contains(requestsSeen[1], message => message.Text.Contains("问题2"));
        Assert.DoesNotContain(requestsSeen[1],
            message => message.Role == Microsoft.Extensions.AI.ChatRole.System);

        // 本轮请求就是用压缩后的历史发的
        Assert.Contains(histories[^1], message => message.Text.Contains("问题2"));
        Assert.Contains(session.Snapshot.Messages, message => message.Text.Contains("已压缩上下文"));
        // 工具循环中途压缩同样要留下摘要气泡
        Assert.Contains(session.Snapshot.Messages, message => message.Kind == ChatMessageKinds.Summary);
    }

    [Fact]
    public async Task CompactCommandCompactsIdleSession()
    {
        // 窗口 64K → 保留预算 32K token
        _settings.DefaultContextWindowTokens = 64 * 1024;
        var histories = new List<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>>();
        var summarized = new List<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>>();
        async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> history)
        {
            histories.Add(history);
            await Task.Yield();
            yield return new LlmTextDelta("回复");
        }

        var llm = new FakeLlm(
            (_, history, _) => StreamAsync(history),
            summary: "要点摘要",
            onSummarize: summarized.Add);
        var manager = CreateManager(llm);
        var session = manager.ActiveSession!;

        // 两轮合计约 40K token：不到自动触发线，但已超过保留预算
        await session.SendAsync(new string('a', 160_000), null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await session.SendAsync("问题2", null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var result = await SlashCommands.ExecuteAsync(session, "/compact");
        Assert.True(result.Success);
        Assert.Contains("已压缩上下文", result.Message);

        // 摘要正文落进一条 summary 气泡：命令路径由 SlashCommands 自己提示结果，这条只放摘要
        var summaryBubble = Assert.Single(
            session.Snapshot.Messages, message => message.Kind == ChatMessageKinds.Summary);
        Assert.Equal("要点摘要", summaryBubble.Text);
        Assert.True(summaryBubble.Expanded);

        // 摘要请求携带的是完整历史（2 问 2 答），而不是被丢弃的那部分
        var summaryInput = Assert.Single(summarized);
        Assert.Equal(4, summaryInput.Count);
        // 压缩这次请求的开销计入会话用量（之前是隐形的）
        Assert.Equal(100, session.Snapshot.Usage?.InputTokens);
        Assert.Equal(20, session.Snapshot.Usage?.OutputTokens);

        // 压缩后的下一轮请求里应带上摘要，且只保留预算内的那一轮原文（问题2）
        await session.SendAsync("问题3", null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var last = histories[^1];
        Assert.Contains(last, message => message.Text.Contains("要点摘要"));
        Assert.Equal(2, last.Count(message => message.Role == Microsoft.Extensions.AI.ChatRole.User));
    }

    [Fact]
    public async Task PublishesContextUsageBeforeEachTurn()
    {
        async IAsyncEnumerable<LlmStreamEvent> StreamAsync()
        {
            await Task.Yield();
            yield return new LlmTextDelta("回复");
        }

        var llm = new FakeLlm((_, _, _) => StreamAsync());
        var manager = CreateManager(llm);
        var session = manager.ActiveSession!;

        var published = new List<ChatContextUsageState?>();
        session.OpsProduced += batch =>
        {
            foreach (var op in batch.Ops)
                if (op is ChatOpContextUsage usage) published.Add(usage.ContextUsage);
        };

        await session.SendAsync("第一问", null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // 请求前估算并推给界面：估算值大于 0，窗口取全局默认值
        var state = Assert.IsType<ChatContextUsageState>(Assert.Single(published));
        Assert.True(state.EstimatedTokens > 0);
        Assert.Equal(_settings.DefaultContextWindowTokens, state.WindowTokens);
        // 触发线由后端统一给出，界面直接比它，不再自己算 90%
        Assert.Equal(ContextCompaction.TriggerTokens(state.WindowTokens), state.TriggerTokens);

        // 快照同样带上上下文占用，页面刷新后底部水位条不会丢
        Assert.Equal(state, session.Snapshot.ContextUsage);
    }

    [Fact]
    public async Task CompactRejectsWhileGenerating()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<LlmStreamEvent> Slow([EnumeratorCancellation] CancellationToken ct)
        {
            await release.Task.WaitAsync(ct);
            yield return new LlmTextDelta("完成");
        }

        var llm = new FakeLlm((_, _, ct) => Slow(ct));
        var manager = CreateManager(llm);
        var session = manager.ActiveSession!;

        await session.SendAsync("问", null, CancellationToken.None);
        var outcome = await session.CompactAsync();

        Assert.Equal(CompactionStatus.Failed, outcome.Status);
        Assert.Contains("正在生成回复", outcome.Message);

        release.SetResult();
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CompactCanBeStoppedBySlashStop()
    {
        // 窗口 64K → 保留预算 32K：第一轮 40K token 已超预算，压缩才有东西可摘要
        _settings.DefaultContextWindowTokens = 64 * 1024;
        // 摘要挂在 await 上：先让压缩真正开始，再用 /stop 打断
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var llm = new FakeLlm(
            (_, _, _) => Streams(new LlmTextDelta("回复")),
            summary: "要点摘要",
            summarizeDelay: async ct => await release.Task.WaitAsync(ct));
        var manager = CreateManager(llm);
        var session = manager.ActiveSession!;

        await session.SendAsync(new string('a', 160_000), null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await session.SendAsync("问题2", null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var compacting = session.CompactAsync();
        await WaitForAsync(() => session.Compacting ? session : null);

        // 压缩期间没有活动 turn，/stop 仍然要能中断（不能回「当前没有正在生成的回复」）
        var stop = await SlashCommands.ExecuteAsync(session, "/stop");
        Assert.Contains("已请求停止压缩上下文", stop.Message);

        var outcome = await compacting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CompactionStatus.Cancelled, outcome.Status);
        release.TrySetResult();

        // 取消后历史保持原样：下一轮请求里没有摘要
        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>? next = null;
        var followUp = new FakeLlm((_, history, _) =>
        {
            next = history;
            return Streams(new LlmTextDelta("好"));
        });
        var manager2 = CreateManager(followUp);
        var session2 = await manager2.OpenAsync(session.Id, CancellationToken.None);
        await session2.SendAsync("问", null, CancellationToken.None);
        await session2.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(next!, message => message.Text.Contains("<conversation_summary>"));
    }

    [Fact]
    public async Task RestoresCompactionSummaryAfterRestart()
    {
        _settings.DefaultContextWindowTokens = 64 * 1024;
        var llm = new FakeLlm((_, _, _) => Streams(new LlmTextDelta("回复")), summary: "重启后要挂回的摘要");
        var manager = CreateManager(llm);
        var session = manager.ActiveSession!;

        await session.SendAsync(new string('a', 160_000), null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await session.SendAsync("问题2", null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var result = await SlashCommands.ExecuteAsync(session, "/compact");
        Assert.True(result.Success);
        var id = session.Id;

        // 重启：同一个 store 新建管理器，摘要应从 summary.md 挂回请求历史开头
        var summaryFile = Path.Combine(_store.SessionDirectory(id), "summary.md");
        Assert.Equal("重启后要挂回的摘要", File.ReadAllText(summaryFile));
        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>? next = null;
        var manager2 = CreateManager(new FakeLlm((_, history, _) =>
        {
            next = history;
            return Streams(new LlmTextDelta("好"));
        }));
        var reopened = await manager2.OpenAsync(id, CancellationToken.None);
        await reopened.SendAsync("重启后的提问", null, CancellationToken.None);
        await reopened.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(next!, message => message.Text.Contains("<conversation_summary>"));
        Assert.Contains(next!, message => message.Text.Contains("重启后要挂回的摘要"));
    }

    [Fact]
    public async Task KeepsAllTurnsWithoutCountTrimming()
    {
        // 不再按轮数自动裁剪：兜底低水位设得很小也照样一轮不丢（旧逻辑超过高水位会一次性裁到它）
        _settings.HistoryKeepTurns = 3;

        var histories = new List<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>>();
        var llm = new FakeLlm((_, history, _) =>
        {
            histories.Add(history);
            return Streams(new LlmTextDelta("回复"));
        });
        var manager = CreateManager(llm);
        var session = manager.ActiveSession!;

        for (var i = 1; i <= 55; i++)
        {
            await session.SendAsync($"问题{i}", null, CancellationToken.None);
            await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        var last = histories[^1];
        Assert.Equal(55, last.Count(message => message.Role == Microsoft.Extensions.AI.ChatRole.User));
        Assert.Contains(last, message => message.Text.Contains("问题1"));
        // 没有触发压缩，也就没有丢弃提示
        Assert.DoesNotContain(session.Snapshot.Messages, message => message.Text.Contains("按轮数丢弃"));
    }

    [Fact]
    public async Task CompactionFailureDropKeepsTheExistingSummary()
    {
        // 压缩失败时的按轮数丢弃会把承载摘要的首条用户消息一起删掉，
        // 必须按 summary.md 重新挂回，否则摘要只存在于磁盘、当前请求里反而丢了。
        _settings.DefaultContextWindowTokens = 64 * 1024;
        _settings.HistoryKeepTurns = 1;

        var histories = new List<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>>();
        var summarizeCalls = 0;
        var llm = new FakeLlm(
            (_, history, _) =>
            {
                histories.Add(history);
                return Streams(new LlmTextDelta("回复"));
            },
            summary: "首次压缩的摘要",
            onSummarize: _ =>
            {
                // 第一次压缩成功，之后每次都失败：走「按轮数丢弃」的兜底
                if (++summarizeCalls > 1) throw new InvalidOperationException("摘要服务不可用");
            });
        var manager = CreateManager(llm);
        var session = manager.ActiveSession!;

        // 第一轮 40K + 第二轮 40K：第二轮请求前自动压缩成功（保留预算 32K，只留最后一轮）
        await session.SendAsync(new string('a', 160_000), null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await session.SendAsync(new string('b', 160_000), null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, summarizeCalls);

        // 第三轮同样超窗：这次压缩失败 → 按轮数丢弃到最近 1 轮，摘要必须重新挂回请求开头
        await session.SendAsync(new string('c', 160_000), null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, summarizeCalls);
        var last = histories[^1];
        Assert.Contains(last, message => message.Text.Contains("<conversation_summary>"));
        Assert.Contains(last, message => message.Text.Contains("首次压缩的摘要"));
        Assert.Equal(1, last.Count(message => message.Role == Microsoft.Extensions.AI.ChatRole.User));
        Assert.Contains(session.Snapshot.Messages, message => message.Text.Contains("按轮数丢弃"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserRequestTextCarriesOnlyOptionalCurrentTime(bool timeAwareness)
    {
        _settings.TimeAwarenessEnabled = timeAwareness;
        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>? captured = null;
        var llm = new FakeLlm((_, history, _) =>
        {
            captured = history;
            return Streams(new LlmTextDelta("好"));
        });
        var manager = CreateManager(llm);
        var session = manager.ActiveSession!;

        await session.SendAsync("在吗", null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var user = Assert.Single(captured!, message => message.Role == Microsoft.Extensions.AI.ChatRole.User);
        if (timeAwareness)
        {
            Assert.StartsWith("在吗", user.Text);
            Assert.Contains("<system_reminder>", user.Text);
            Assert.Contains("当前时间", user.Text);
        }
        else
        {
            Assert.Equal("在吗", user.Text);
        }

        // 旧对话不再自动注入：请求里只有可能的时间信息，没有「相关回忆」块
        Assert.DoesNotContain("相关回忆", user.Text);
        Assert.DoesNotContain("以下是与本轮提问相关的旧对话", user.Text);
    }

    [Fact]
    public async Task SystemNoticeKeepsRawTextForExpansion()
    {
        var manager = CreateManager(new FakeLlm((_, _, _) => Streams(new LlmTextDelta("收到"))));
        var session = manager.ActiveSession!;
        const string prompt = "<scheduled_task name=\"日报\">\n整理今天的日报\n</scheduled_task>";
        const string display = "⏰ 定时任务「日报」：整理今天的日报";

        await session.EnqueueSystemTurnAsync(prompt, display, isPlatformNotice: true);
        var notice = await WaitForAsync(() =>
            session.Snapshot.Messages.FirstOrDefault(message => message.Kind == ChatMessageKinds.Notice));
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(display, notice.Text);
        Assert.Equal(prompt, notice.RawText);

        // 重新打开会话后，摘要与原始文本都要恢复
        var id = session.Id;
        await manager.DisposeAsync();
        _manager = null!;

        var reloaded = new ChatSessionManager(_settings, _factory, _store);
        try
        {
            var opened = await reloaded.OpenAsync(id, CancellationToken.None);
            var restored = opened.Snapshot.Messages.First(message => message.Kind == ChatMessageKinds.Notice);
            Assert.Equal(display, restored.Text);
            Assert.Equal(prompt, restored.RawText);

            // 只有提醒气泡带原文：普通用户/助手消息不下发重复的原始文本
            Assert.All(opened.Snapshot.Messages.Where(message => message.Kind != ChatMessageKinds.Notice),
                message => Assert.Null(message.RawText));
        }
        finally
        {
            await reloaded.DisposeAsync();
        }
    }

    [Fact]
    public async Task CloseUnloadsIdleSessionAndReopensFromDisk()
    {
        var manager = CreateManager(new FakeLlm((_, _, _) => Streams(new LlmTextDelta("好"))));
        var first = manager.ActiveSession!;
        await first.SendAsync("第一句", null, CancellationToken.None);
        await first.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var second = await manager.CreateAsync(null, CancellationToken.None);
        await manager.OpenAsync(second.Id, CancellationToken.None);

        // 活动会话不能关闭
        await Assert.ThrowsAsync<ChatSessionException>(() =>
            manager.CloseAsync(second.Id, CancellationToken.None));

        await manager.CloseAsync(first.Id, CancellationToken.None);
        Assert.DoesNotContain(manager.OpenSessions, session => session.Id == first.Id);

        // 卸载只释放内存，历史仍从磁盘恢复
        var reopened = await manager.OpenAsync(first.Id, CancellationToken.None);
        Assert.Contains(reopened.Snapshot.Messages, message => message.Text == "第一句");
    }

    [Fact]
    public async Task ApprovalDecisionAllowsToolToContinue()
    {
        var llm = new FakeLlm((_, _, ct) => ApprovalStreamAsync(ct));
        var manager = CreateManager(llm);
        var session = manager.ActiveSession!;

        async IAsyncEnumerable<LlmStreamEvent> ApprovalStreamAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var approved = await _access.RequestApprovalAsync("pwsh", "echo hi", ct);
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
        Assert.Equal("读取 · a.txt", tool.Tool.Label);
        Assert.Equal("读完了", messages.Last().Text);
    }

    [Fact]
    public async Task ToolLabelIsPersistedAndRestored()
    {
        var llm = new FakeLlm((_, _, _) => Streams(
            new LlmToolCallStartedEvent("call-1", "pwsh", "{\"command\":\"npm test\"}"),
            new LlmToolCallEvent("call-1", "pwsh", "{\"command\":\"npm test\"}", "ok"),
            new LlmTextDelta("完成")));
        var manager = CreateManager(llm);
        var session = manager.ActiveSession!;

        await session.SendAsync("跑测试", null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var tool = session.Snapshot.Messages.First(message => message.Kind == ChatMessageKinds.Tool);
        Assert.Equal("运行命令 · npm test", tool.Tool!.Label);

        var id = session.Id;
        await manager.DisposeAsync();
        _manager = null!;

        var reloaded = new ChatSessionManager(_settings, _factory, _store);
        try
        {
            var opened = await reloaded.OpenAsync(id, CancellationToken.None);
            var restored = opened.Snapshot.Messages.First(message => message.Kind == ChatMessageKinds.Tool);
            Assert.Equal("运行命令 · npm test", restored.Tool!.Label);
        }
        finally
        {
            await reloaded.DisposeAsync();
        }
    }

    [Fact]
    public async Task FileEditDiffsAreAttachedToToolBubbleAndRestored()
    {
        var diff = new ChatFileDiffState("a.txt", 1, 1, false,
        [
            new ChatDiffHunkState(1, 1, 1, 1,
            [
                new ChatDiffLineState("del", "old"),
                new ChatDiffLineState("add", "new"),
            ]),
        ]);
        var llm = new FakeLlm((_, _, _) => DiffStreamAsync());
        var manager = CreateManager(llm);
        var session = manager.ActiveSession!;
        string? backupDirectory = null;

        async IAsyncEnumerable<LlmStreamEvent> DiffStreamAsync()
        {
            yield return new LlmToolCallStartedEvent("call-1", "edit_file", "{\"path\":\"a.txt\"}");
            backupDirectory = ToolInvocationContext.BackupDirectory;
            _files.RecordEdit(new ChatFileEdit("call-1", [diff]));
            yield return new LlmToolCallEvent("call-1", "edit_file", "{\"path\":\"a.txt\"}", "已编辑");
            yield return new LlmTextDelta("完成");
        }

        await session.SendAsync("改文件", null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(_store.SessionDirectory(session.Id), backupDirectory);
        var tool = session.Snapshot.Messages.First(message => message.Kind == ChatMessageKinds.Tool);
        var attached = Assert.Single(tool.Tool!.Diffs!);
        Assert.Equal("a.txt", attached.Path);
        Assert.Equal(1, attached.Added);
        Assert.Equal(1, attached.Removed);
        var hunk = Assert.Single(attached.Hunks);
        Assert.Equal(("del", "old"), (hunk.Lines[0].Kind, hunk.Lines[0].Text));

        var id = session.Id;
        var conversation = await File.ReadAllTextAsync(_store.ConversationPath(id));
        Assert.DoesNotContain("\"Hunks\"", conversation);
        Assert.Contains("\"DiffFile\"", conversation);
        var diffFile = Assert.Single(Directory.GetFiles(
            Path.Combine(_store.SessionDirectory(id), "diffs"), "*.json"));
        Assert.Contains("\"Lines\"", await File.ReadAllTextAsync(diffFile));
        await manager.DisposeAsync();
        _manager = null!;

        var reloaded = new ChatSessionManager(_settings, _factory, _store);
        try
        {
            var opened = await reloaded.OpenAsync(id, CancellationToken.None);
            var restored = opened.Snapshot.Messages.First(message => message.Kind == ChatMessageKinds.Tool);
            var restoredDiff = Assert.Single(restored.Tool!.Diffs!);
            Assert.Equal("a.txt", restoredDiff.Path);
            Assert.Equal(("add", "new"), (restoredDiff.Hunks[0].Lines[1].Kind, restoredDiff.Hunks[0].Lines[1].Text));
        }
        finally
        {
            await reloaded.DisposeAsync();
        }
    }

    [Fact]
    public async Task ShownFileBubbleIsRestoredAfterRestart()
    {
        var file = Path.Combine(_root, "notes.md");
        await File.WriteAllTextAsync(file, "hello");
        var args = $"{{\"path\":{JsonSerializer.Serialize(file)},\"displayName\":\"笔记\"}}";

        var llm = new FakeLlm((_, _, _) => StreamAsync());
        var manager = CreateManager(llm);
        var session = manager.ActiveSession!;

        async IAsyncEnumerable<LlmStreamEvent> StreamAsync()
        {
            yield return new LlmToolCallStartedEvent("call-1", "show_file", args);
            _files.Show(new ChatFileDisplay(file, "笔记", false));
            yield return new LlmToolCallEvent("call-1", "show_file", args, "已在对话窗口展示文件「笔记」。");
            yield return new LlmTextDelta("完成");
        }

        await session.SendAsync("看看笔记", null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var shown = session.Snapshot.Messages.First(message => message.Kind == ChatMessageKinds.Attachment);
        Assert.Equal(("笔记", file), (shown.Attachment!.Name, shown.Attachment.Path));

        var id = session.Id;
        await manager.DisposeAsync();
        _manager = null!;

        var reloaded = new ChatSessionManager(_settings, _factory, _store);
        try
        {
            var opened = await reloaded.OpenAsync(id, CancellationToken.None);
            var restored = opened.Snapshot.Messages.First(message => message.Kind == ChatMessageKinds.Attachment);
            Assert.Equal(("笔记", file, false), (restored.Attachment!.Name, restored.Attachment.Path,
                restored.Attachment.IsImage));
            Assert.Equal(5L, restored.Attachment.Size);
        }
        finally
        {
            await reloaded.DisposeAsync();
        }
    }

    [Fact]
    public async Task ManagerCreatesRenamesSearchesAndDeletesWithRecycleBin()
    {
        var manager = CreateManager(new FakeLlm((_, _, _) => Streams(new LlmTextDelta("ok"))));

        // 先发一条消息，得到一个已落盘的会话（全新目录里只有一个内存草稿）
        var firstDraft = manager.ActiveSession!;
        await firstDraft.SendAsync("旧会话第一条", null, CancellationToken.None);
        await firstDraft.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var first = Assert.Single(manager.Sessions);
        Assert.Equal("旧会话第一条", first.Title);

        // 新建只是内存草稿：不进列表、不建目录
        var created = await manager.CreateAsync(null, CancellationToken.None);
        Assert.DoesNotContain(manager.Sessions, record => record.Id == created.Id);
        Assert.False(Directory.Exists(_store.SessionDirectory(created.Id)));

        // 发出第一条消息才落盘并进入列表
        var draft = manager.ActiveSession!;
        await draft.SendAsync("第一条消息", null, CancellationToken.None);
        await draft.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(manager.Sessions, record => record.Id == created.Id);
        Assert.True(Directory.Exists(_store.SessionDirectory(created.Id)));

        await manager.RenameAsync(created.Id, "改过的标题", CancellationToken.None);
        var search = await manager.SearchAsync("改过", CancellationToken.None);
        Assert.Contains(search, record => record.Id == created.Id);

        var payload = await manager.DeleteAsync(created.Id, CancellationToken.None);
        Assert.DoesNotContain(payload.Sessions, record => record.Id == created.Id);
        Assert.Equal(first.Id, payload.ActiveId);
        Assert.True(Directory.Exists(_store.TrashDirectory));

        var opened = await manager.OpenAsync(first.Id, CancellationToken.None);
        Assert.Equal(2, opened.Snapshot.Messages.Count);
    }

    [Fact]
    public async Task ScheduledInboxRoutesToWorkspaceAndAppliesModelWithoutChangingDefault()
    {
        var manager = CreateManager(new FakeLlm((_, _, _) => Streams(new LlmTextDelta("ok"))));
        var workspace = Path.Combine(_root, "ws-scheduled");
        Directory.CreateDirectory(workspace);

        _settings.ChatProviders[0].Models.Add(
            new ChatProviderModel { ModelId = "other", Name = "另一个模型", IsActive = true });
        _settings.RebuildChatModels();
        var defaultKey = _settings.ChatModelKey;
        var modelKey = _settings.ChatModels.First(model => model.Key != defaultKey).Key;

        var inbox = await manager.GetOrCreateScheduledInboxAsync(workspace, modelKey, CancellationToken.None);
        Assert.Equal(Path.GetFullPath(workspace), inbox.Snapshot.Workspace?.Path);
        Assert.Equal(modelKey, inbox.Snapshot.SelectedModelKey);
        // 后台投递切换会话模型，但不能改掉用户的全局默认模型
        Assert.Equal(defaultKey, _settings.ChatModelKey);

        // 同一工作区复用同一个定时任务会话，未指定模型时保持原模型
        var again = await manager.GetOrCreateScheduledInboxAsync(workspace, null, CancellationToken.None);
        Assert.Equal(inbox.Id, again.Id);
        Assert.Equal(modelKey, again.Snapshot.SelectedModelKey);

        // 未指定工作区仍进入全局定时任务会话
        var global = await manager.GetOrCreateScheduledInboxAsync(null, null, CancellationToken.None);
        Assert.NotEqual(inbox.Id, global.Id);
        Assert.Null(global.Snapshot.Workspace);
    }

    [Fact]
    public async Task ResolveAsyncDoesNotChangeActiveSession()
    {
        var manager = CreateManager(new FakeLlm((_, _, _) => Streams(new LlmTextDelta("ok"))));

        // 先让草稿发出一轮消息，得到一个已落盘的会话
        var firstDraft = manager.ActiveSession!;
        await firstDraft.SendAsync("旧会话", null, CancellationToken.None);
        await firstDraft.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var first = Assert.Single(manager.Sessions);

        // 新建草稿会把活动会话切到草稿上
        var draft = await manager.CreateAsync(null, CancellationToken.None);
        var activeBefore = manager.ActiveSessionId;
        Assert.Equal(draft.Id, activeBefore);

        // 按 id 解析只返回目标实例，不改变活动会话（chat.* 带 sessionId 时走这条路）
        var resolved = await manager.ResolveAsync(first.Id, CancellationToken.None);
        Assert.Same(firstDraft, resolved);
        Assert.Equal(activeBefore, manager.ActiveSessionId);

        await manager.DisposeAsync();
        _manager = null!;
    }

    [Fact]
    public async Task DraftSessionIsNotPersistedUntilFirstMessage()
    {
        var manager = CreateManager(new FakeLlm((_, _, _) => Streams(new LlmTextDelta("ok"))));

        // 全新的数据目录：只有一个内存草稿，索引与目录都是空的
        Assert.Empty(manager.Sessions);
        var draft = manager.ActiveSession;
        Assert.NotNull(draft);
        Assert.False(Directory.Exists(_store.SessionDirectory(draft!.Id)));
        var indexJson = await File.ReadAllTextAsync(_store.IndexPath);
        Assert.DoesNotContain(draft.Id, indexJson);

        await draft.SendAsync("草稿转正", null, CancellationToken.None);
        await draft.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var record = Assert.Single(manager.Sessions);
        Assert.Equal(draft.Id, record.Id);
        Assert.Equal("草稿转正", record.Title);
        Assert.True(File.Exists(_store.ConversationPath(draft.Id)));
        indexJson = await File.ReadAllTextAsync(_store.IndexPath);
        Assert.Contains(draft.Id, indexJson);
    }

    [Fact]
    public async Task RepeatedCreateKeepsOnlyOneInvisibleDraft()
    {
        var manager = CreateManager(new FakeLlm((_, _, _) => Streams(new LlmTextDelta("ok"))));
        Assert.Empty(manager.Sessions);

        var first = await manager.CreateAsync(null, CancellationToken.None);
        var second = await manager.CreateAsync(null, CancellationToken.None);
        // 已经停在空白草稿上：复用同一个，不会堆出第二个隐形会话
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(second.Id, manager.ActiveSessionId);
        Assert.Empty(manager.Sessions);
        Assert.False(Directory.Exists(_store.SessionDirectory(first.Id)));
        Assert.False(Directory.Exists(_store.SessionDirectory(second.Id)));
    }

    [Fact]
    public async Task SessionsAreGroupedByWorkspaceAndMoveOnSwitch()
    {
        var workspaceA = Path.Combine(_root, "workspace-a");
        var workspaceB = Path.Combine(_root, "workspace-b");
        Directory.CreateDirectory(workspaceA);
        Directory.CreateDirectory(workspaceB);

        var manager = CreateManager(new FakeLlm((_, _, _) => Streams(new LlmTextDelta("ok"))));
        var record = await manager.CreateAsync(workspaceA, CancellationToken.None);
        var session = manager.ActiveSession!;
        await session.SendAsync("工作区分组", null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var directoryA = _store.SessionDirectory(record.Id, workspaceA);
        Assert.True(File.Exists(Path.Combine(directoryA, "conversation.jsonl")));
        Assert.True(directoryA.Contains(ChatSessionStore.WorkspaceGroupName(workspaceA), StringComparison.Ordinal));

        await session.SwitchWorkspaceAsync(workspaceB);

        var directoryB = _store.SessionDirectory(record.Id, workspaceB);
        Assert.True(File.Exists(Path.Combine(directoryB, "conversation.jsonl")));
        Assert.False(Directory.Exists(directoryA));
        Assert.True(directoryB.Contains(ChatSessionStore.WorkspaceGroupName(workspaceB), StringComparison.Ordinal));
        Assert.Equal(workspaceB, Assert.Single(manager.Sessions).WorkspacePath);

        await manager.DisposeAsync();
        _manager = null!;

        var reloaded = new ChatSessionManager(_settings, _factory, _store);
        try
        {
            var opened = await reloaded.OpenAsync(record.Id, CancellationToken.None);
            Assert.Equal(2, opened.Snapshot.Messages.Count);
            Assert.Equal(workspaceB, opened.Snapshot.Workspace?.Path);
        }
        finally
        {
            await reloaded.DisposeAsync();
        }

        // 索引丢失时也能从分组目录 + workspace.txt 重建，工作区分组不丢
        File.Delete(_store.IndexPath);
        var rebuilt = new ChatSessionManager(_settings, _factory, _store);
        try
        {
            var restored = Assert.Single(rebuilt.Sessions);
            Assert.Equal(record.Id, restored.Id);
            Assert.Equal(workspaceB, restored.WorkspacePath);
            Assert.Equal(2, (await rebuilt.OpenAsync(record.Id, CancellationToken.None)).Snapshot.Messages.Count);
        }
        finally
        {
            await rebuilt.DisposeAsync();
        }
    }

    [Fact]
    public async Task WorkspaceSwitchMovesDiffSidecarAndKeepsItReadable()
    {
        var workspaceA = Path.Combine(_root, "diff-workspace-a");
        var workspaceB = Path.Combine(_root, "diff-workspace-b");
        Directory.CreateDirectory(workspaceA);
        Directory.CreateDirectory(workspaceB);

        var diff = new ChatFileDiffState("a.txt", 1, 0, false,
        [
            new ChatDiffHunkState(1, 0, 1, 1, [new ChatDiffLineState("add", "x")]),
        ]);
        var llm = new FakeLlm((_, _, _) => DiffStreamAsync());
        var manager = CreateManager(llm);
        var record = await manager.CreateAsync(workspaceA, CancellationToken.None);
        var session = manager.ActiveSession!;

        async IAsyncEnumerable<LlmStreamEvent> DiffStreamAsync()
        {
            yield return new LlmToolCallStartedEvent("call-1", "edit_file", "{\"path\":\"a.txt\"}");
            _files.RecordEdit(new ChatFileEdit("call-1", [diff]));
            yield return new LlmToolCallEvent("call-1", "edit_file", "{\"path\":\"a.txt\"}", "已编辑");
            yield return new LlmTextDelta("完成");
        }

        await session.SendAsync("改文件", null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var directoryA = _store.SessionDirectory(record.Id, workspaceA);
        Assert.Single(Directory.GetFiles(Path.Combine(directoryA, "diffs"), "*.json"));

        await session.SwitchWorkspaceAsync(workspaceB);

        var directoryB = _store.SessionDirectory(record.Id, workspaceB);
        Assert.False(Directory.Exists(directoryA));
        Assert.Single(Directory.GetFiles(Path.Combine(directoryB, "diffs"), "*.json"));

        await manager.DisposeAsync();
        _manager = null!;

        var reloaded = new ChatSessionManager(_settings, _factory, _store);
        try
        {
            var opened = await reloaded.OpenAsync(record.Id, CancellationToken.None);
            var tool = opened.Snapshot.Messages.First(message => message.Kind == ChatMessageKinds.Tool);
            var restored = Assert.Single(tool.Tool!.Diffs!);
            Assert.Equal(("add", "x"), (restored.Hunks[0].Lines[0].Kind, restored.Hunks[0].Lines[0].Text));
        }
        finally
        {
            await reloaded.DisposeAsync();
        }
    }

    [Fact]
    public async Task SwitchingWorkspaceDoesNotChangeProcessCurrentDirectory()
    {
        var workspace = Path.Combine(_root, "workspace-cwd");
        var other = Path.Combine(_root, "workspace-cwd-other");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(other);

        var manager = CreateManager(new FakeLlm((_, _, _) => Streams(new LlmTextDelta("ok"))));
        var before = Directory.GetCurrentDirectory();

        var record = await manager.CreateAsync(workspace, CancellationToken.None);
        var session = manager.ActiveSession!;
        Assert.Equal(before, Directory.GetCurrentDirectory());

        await session.SwitchWorkspaceAsync(other);
        Assert.Equal(before, Directory.GetCurrentDirectory());

        await manager.OpenAsync(record.Id, CancellationToken.None);
        Assert.Equal(before, Directory.GetCurrentDirectory());
    }

    [Fact]
    public async Task TurnUsesSessionWorkspaceAsLogicalCurrentDirectory()
    {
        var workspace = Path.Combine(_root, "workspace-logical");
        Directory.CreateDirectory(workspace);

        string? seen = null;
        var manager = CreateManager(new FakeLlm((_, _, _) =>
        {
            seen = WorkspaceContext.Current;
            return Streams(new LlmTextDelta("ok"));
        }));
        await manager.CreateAsync(workspace, CancellationToken.None);

        var session = manager.ActiveSession!;
        await session.SendAsync("逻辑工作区", null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(workspace, seen);
        // 轮次结束后回到无会话上下文状态，不留全局痕迹
        Assert.Equal(AppContext.BaseDirectory, WorkspaceContext.Current);
    }

    [Fact]
    public async Task ProfileIsSharedAcrossSessionMemories()
    {
        var profile = Path.Combine(_root, "memory.json");
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
    public void SummaryIsStoredAsPlainMarkdown()
    {
        var root = Path.Combine(_root, "summary-md");
        var profile = Path.Combine(_root, "memory.json");
        var memory = new ChatMemory(_settings, root, "conversation.jsonl", profile);
        const string body = "## 要点\n\n- 用户在做上下文压缩\n- 已确认 128K 窗口";

        memory.SaveSummary(body);

        // 落盘的就是模型写出的 Markdown 原文：没有 JSON 包装，换行原样保留
        var file = Path.Combine(root, "summary.md");
        Assert.True(File.Exists(file));
        Assert.Equal(body, File.ReadAllText(file));
        Assert.False(File.Exists(Path.Combine(root, "summary.json")));
        // 内存里仍然是可直接挂进请求历史的摘要块
        Assert.Equal($"<conversation_summary>\n{body}\n</conversation_summary>", memory.Summary);

        // 重启后按同一份 Markdown 重建摘要块
        var reopened = new ChatMemory(_settings, root, "conversation.jsonl", profile);
        Assert.Equal(memory.Summary, reopened.Summary);

        // 清除时文件一起删掉
        reopened.ClearSummary();
        Assert.False(File.Exists(file));
        Assert.Null(reopened.Summary);
    }

    [Fact]
    public async Task ApprovalIsRoutedToCurrentSessionWithMultipleLoaded()
    {
        var llm = new FakeLlm((_, _, ct) => ApprovalStreamAsync(ct));
        var manager = CreateManager(llm);
        var first = manager.ActiveSession!;

        async IAsyncEnumerable<LlmStreamEvent> ApprovalStreamAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var approved = await _access.RequestApprovalAsync("pwsh", "echo two", ct);
            await Task.Yield();
            yield return new LlmTextDelta(approved ? "允许执行" : "拒绝执行");
        }

        // 先让第一个会话发一条消息落盘，它仍然保持打开并订阅着审批事件
        await first.SendAsync("第一个会话跑命令", null, CancellationToken.None);
        var firstApproval = await WaitForAsync(() =>
            first.Snapshot.Messages.FirstOrDefault(message => message.Kind == ChatMessageKinds.Approval));
        await first.DecideApprovalAsync(firstApproval.Id, approve: true);
        await first.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(manager.Sessions);

        // 再开一个新会话（草稿）；审批必须仍落到真正在生成的新会话
        var second = await manager.CreateAsync(null, CancellationToken.None);
        var active = manager.ActiveSession!;
        Assert.NotEqual(first.Id, active.Id);

        await active.SendAsync("在第二个会话跑命令", null, CancellationToken.None);
        var approval = await WaitForAsync(() =>
            active.Snapshot.Messages.FirstOrDefault(message => message.Kind == ChatMessageKinds.Approval));
        await active.DecideApprovalAsync(approval.Id, approve: true);
        await active.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("允许执行", active.Snapshot.Messages.Last().Text);
        Assert.DoesNotContain(first.Snapshot.Messages, message => message.Id == approval.Id);
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
    public async Task CleanupRemovesAttachmentsOlderThanSevenDays()
    {
        var directory = Path.Combine(ChatMemory.SouffleHome, "attachments", "old-session");
        Directory.CreateDirectory(directory);
        var expired = Path.Combine(directory, "expired.txt");
        var fresh = Path.Combine(directory, "fresh.txt");
        await File.WriteAllTextAsync(expired, "旧附件");
        await File.WriteAllTextAsync(fresh, "新附件");
        File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-8));
        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow.AddDays(-1));

        ChatSession.CleanupExpiredAttachments();

        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public async Task DeletingActiveSessionSwitchesToRemainingThenCreatesReplacement()
    {
        var manager = CreateManager(new FakeLlm((_, _, _) => Streams(new LlmTextDelta("ok"))));

        // 先把第一个会话发条消息落盘（全新的数据目录只有一个草稿）
        var draft = manager.ActiveSession!;
        await draft.SendAsync("第一个会话", null, CancellationToken.None);
        await draft.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var first = Assert.Single(manager.Sessions);

        var second = await manager.CreateAsync(null, CancellationToken.None);
        Assert.Equal(second.Id, manager.ActiveSessionId);
        Assert.Single(manager.Sessions); // 草稿还没进列表

        var afterSecond = await manager.DeleteAsync(second.Id, CancellationToken.None);
        Assert.Equal(first.Id, afterSecond.ActiveId);
        Assert.Single(afterSecond.Sessions);

        var afterFirst = await manager.DeleteAsync(first.Id, CancellationToken.None);
        Assert.NotNull(afterFirst.ActiveId);
        Assert.NotEqual(first.Id, afterFirst.ActiveId);
        Assert.Empty(afterFirst.Sessions); // 替代品是内存草稿
        Assert.NotNull(manager.ActiveSession);
        Assert.True(Directory.Exists(_store.TrashDirectory));
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
            IAsyncEnumerable<LlmStreamEvent>> stream,
        Func<ChatModelConfig, IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>, int>? estimate = null,
        string summary = "已压缩的对话摘要",
        Action<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>>? onSummarize = null,
        Func<ITurnContextGuard, IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>,
            Task<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>?>>? guard = null,
        Func<CancellationToken, Task>? summarizeDelay = null)
        : ILlmChatService
    {
        /// <summary>模拟工具循环：真服务在下一轮请求前会问一次守卫，这里在开播前问一次。</summary>
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            ChatModelConfig model,
            IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> history,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            ITurnContextGuard? contextGuard = null)
        {
            var effectiveHistory = guard is null || contextGuard is null
                ? history
                : await guard(contextGuard, history) ?? history;
            await foreach (var evt in stream(model, effectiveHistory, cancellationToken))
                yield return evt;
        }

        public Task<int> EstimateContextTokensAsync(
            ChatModelConfig model,
            IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> history,
            CancellationToken cancellationToken) =>
            Task.FromResult(estimate?.Invoke(model, history)
                            ?? SoufflePet.Services.TokenEstimator.EstimateMessages(history));

        public async IAsyncEnumerable<LlmStreamEvent> SummarizeAsync(
            ChatModelConfig model,
            IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> history,
            string instruction,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            onSummarize?.Invoke(history);
            if (summarizeDelay is not null) await summarizeDelay(cancellationToken);
            await Task.Yield();
            yield return new LlmTextDelta(summary);
            // 真服务的摘要请求同样带用量：压缩开销必须能被计入会话用量与统计
            yield return new LlmUsageEvent(100, 20, 0);
        }
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
