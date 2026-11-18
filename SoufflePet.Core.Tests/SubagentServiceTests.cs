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
using SoufflePet.Services;
using Xunit;

namespace SoufflePet.Core.Tests;

/// <summary>子代理服务：子会话创建、立即返回、等待结论、继续派活与嵌套深度闸门。</summary>
public sealed class SubagentServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "soufflepet-subagent-tests", Guid.NewGuid().ToString("N"));

    private SouffleSettings _settings = null!;
    private ChatToolAccess _access = null!;
    private UserQuestionService _questions = null!;
    private ChatFileService _files = null!;
    private ChatSessionStore _store = null!;
    private ChatSessionFactory _factory = null!;
    private ChatSessionManager _manager = null!;
    private SubagentService _service = null!;

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
        _store = new ChatSessionStore(Path.Combine(_root, "sessions"), Path.Combine(_root, "memory.json"));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_manager is not null) await _manager.DisposeAsync();
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
    public async Task SpawnReturnsImmediatelyAndWaitCollectsChildConclusion()
    {
        string? spawnResult = null;
        string? waitResult = null;
        string? parentId = null;
        string? activeDuringSpawn = null;
        IReadOnlyList<SessionRecord>? sessionsDuringSpawn = null;

        var llm = new FakeLlm((_, history, ct) => StreamAsync(history, ct));
        CreateManager(llm);
        async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> history,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var prompt = history.LastOrDefault(message => message.Role == Microsoft.Extensions.AI.ChatRole.User)
                ?.Text ?? string.Empty;

            if (prompt.StartsWith("PARENT", StringComparison.Ordinal))
            {
                activeDuringSpawn = _manager.ActiveSessionId;
                spawnResult = await _service.SpawnAsync(new SubagentRequest("CHILD task", "调研缓存口径"), ct);
                sessionsDuringSpawn = _manager.Sessions;
                waitResult = await _service.WaitAsync(null, 10_000, ct);
                yield return new LlmTextDelta("父结论");
                yield break;
            }

            await Task.Yield();
            yield return new LlmTextDelta("子结论");
        }

        _service = new SubagentService(_settings, () => _manager);
        var parent = _manager.ActiveSession!;
        parentId = parent.Id;

        await parent.SendAsync("PARENT question", null, CancellationToken.None);
        await parent.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(spawnResult);
        using var payload = System.Text.Json.JsonDocument.Parse(spawnResult!);
        Assert.Equal("started", payload.RootElement.GetProperty("status").GetString());
        // 创建子代理不切换父会话
        Assert.Equal(parentId, activeDuringSpawn);
        Assert.Equal(parentId, _manager.ActiveSessionId);

        var child = Assert.Single(_manager.ChildSessions(parentId!));
        Assert.Equal(child.Id, payload.RootElement.GetProperty("sessionId").GetString());
        Assert.Contains(sessionsDuringSpawn!, record => record.Id == child.Id);
        Assert.Contains("子结论", waitResult!);
        Assert.Contains(child.Title, waitResult!);
        Assert.Contains("父结论", parent.LastAssistantText!);
    }

    [Fact]
    public async Task SendMessageStartsAnotherTurnForIdleChild()
    {
        var waitResults = new List<string>();
        var llm = new FakeLlm((_, history, ct) => StreamAsync(history, ct));
        CreateManager(llm);
        async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> history,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var prompt = history.LastOrDefault(message => message.Role == Microsoft.Extensions.AI.ChatRole.User)
                ?.Text ?? string.Empty;

            if (prompt.StartsWith("PARENT", StringComparison.Ordinal))
            {
                await _service.SpawnAsync(new SubagentRequest("CHILD first", "探索"), ct);
                waitResults.Add(await _service.WaitAsync("探索", 10_000, ct));
                await _service.SendAsync("探索", "CHILD second", ct);
                waitResults.Add(await _service.WaitAsync("探索", 10_000, ct));
                yield return new LlmTextDelta("父结论");
                yield break;
            }

            await Task.Yield();
            yield return new LlmTextDelta(prompt.Contains("second", StringComparison.Ordinal) ? "第二轮结论" : "第一轮结论");
        }

        _service = new SubagentService(_settings, () => _manager);
        var parent = _manager.ActiveSession!;
        await parent.SendAsync("PARENT question", null, CancellationToken.None);
        await parent.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, waitResults.Count);
        Assert.Contains("第一轮结论", waitResults[0]);
        Assert.Contains("第二轮结论", waitResults[1]);
    }

    [Fact]
    public async Task SendWhileBusyQueuesAndWaitCoversQueuedTurn()
    {
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? sendResult = null;
        string? waitResult = null;

        var llm = new FakeLlm((_, history, ct) => StreamAsync(history, ct));
        CreateManager(llm);

        async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> history,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var prompt = history.LastOrDefault(message => message.Role == Microsoft.Extensions.AI.ChatRole.User)
                ?.Text ?? string.Empty;

            if (prompt.StartsWith("PARENT", StringComparison.Ordinal))
            {
                await _service.SpawnAsync(new SubagentRequest("CHILD first", "探索"), ct);
                sendResult = await _service.SendAsync("探索", "CHILD second", ct);
                // 子代理仍在执行，此时等待必须持续到排队轮次结束
                releaseFirst.TrySetResult();
                waitResult = await _service.WaitAsync("探索", 10_000, ct);
                yield return new LlmTextDelta("父结论");
                yield break;
            }

            if (prompt.Contains("first", StringComparison.Ordinal))
            {
                await releaseFirst.Task.WaitAsync(ct);
                yield return new LlmTextDelta("第一轮结论");
                yield break;
            }

            await Task.Yield();
            yield return new LlmTextDelta("排队结论");
        }

        _service = new SubagentService(_settings, () => _manager);
        var parent = _manager.ActiveSession!;
        await parent.SendAsync("PARENT question", null, CancellationToken.None);
        await parent.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("已排队", sendResult!);
        Assert.Contains("排队结论", waitResult!);
        Assert.DoesNotContain("第一轮结论", waitResult!);
    }

    [Fact]
    public async Task DepthGateRejectsNestedSubagent()
    {
        _settings.MaxSubagentDepth = 1;
        string? nestedResult = null;
        var llm = new FakeLlm((_, history, ct) => StreamAsync(history, ct));
        CreateManager(llm);
        async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> history,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var prompt = history.LastOrDefault(message => message.Role == Microsoft.Extensions.AI.ChatRole.User)
                ?.Text ?? string.Empty;

            if (prompt.StartsWith("PARENT", StringComparison.Ordinal))
            {
                await _service.SpawnAsync(new SubagentRequest("CHILD task", "探索"), ct);
                await _service.WaitAsync(null, 10_000, ct);
                yield return new LlmTextDelta("父结论");
                yield break;
            }

            // 子代理再派生：深度已到上限，应被拒绝且不创建新的子会话
            nestedResult = await _service.SpawnAsync(new SubagentRequest("GRANDCHILD task", "更深"), ct);
            await Task.Yield();
            yield return new LlmTextDelta("子结论");
        }

        _service = new SubagentService(_settings, () => _manager);
        var parent = _manager.ActiveSession!;
        await parent.SendAsync("PARENT question", null, CancellationToken.None);
        await parent.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("最大嵌套深度", nestedResult!);
        var child = Assert.Single(_manager.ChildSessions(parent.Id));
        Assert.Empty(_manager.ChildSessions(child.Id));
    }

    [Fact]
    public async Task SpawnOutsideTurnIsRejected()
    {
        CreateManager(new FakeLlm((_, _, _) => StreamAsync()));
        var service = new SubagentService(_settings, () => _manager);

        var result = await service.SpawnAsync(new SubagentRequest("task", "描述"), CancellationToken.None);

        Assert.Contains("当前没有可挂载的会话", result);
        Assert.Empty(_manager.Sessions);
        await _manager.DisposeAsync();
        _manager = null!;

        static async IAsyncEnumerable<LlmStreamEvent> StreamAsync()
        {
            yield return new LlmTextDelta("不应出现");
            await Task.Yield();
        }
    }

    [Fact]
    public async Task DeletingParentRemovesChildAndGrandchildSessions()
    {
        CreateManager(new FakeLlm((_, _, _) => StreamAsync()));
        var parent = _manager.ActiveSession!;

        var child = await _manager.CreateChildAsync(parent.Id, "子任务", null, CancellationToken.None);
        var grandchild = await _manager.CreateChildAsync(child.Id, "更深", null, CancellationToken.None);

        await _manager.DeleteAsync(parent.Id, CancellationToken.None);

        var remaining = _manager.Sessions;
        Assert.DoesNotContain(remaining, record => record.Id == parent.Id);
        Assert.DoesNotContain(remaining, record => record.Id == child.Id);
        Assert.DoesNotContain(remaining, record => record.Id == grandchild.Id);
        // 子代理会话不能成为新的活动会话：删完会补一个普通草稿
        Assert.NotEqual(child.Id, _manager.ActiveSessionId);

        static async IAsyncEnumerable<LlmStreamEvent> StreamAsync()
        {
            yield return new LlmTextDelta("不应出现");
            await Task.Yield();
        }
    }

    private void CreateManager(FakeLlm llm)
    {
        _factory = new ChatSessionFactory(_settings, llm, _access, _questions, _files, new FakeStatistics());
        _manager = new ChatSessionManager(_settings, _factory, _store);
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
}
