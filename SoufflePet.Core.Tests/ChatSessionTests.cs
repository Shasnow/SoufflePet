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
    public async Task UsageIncludesSessionStats()
    {
        var llm = new FakeLlm((_, _, _) => Streams(
            new LlmTextDelta("你好"),
            new LlmUsageEvent(11, 3, 2, Steps: 2, ModelMilliseconds: 12_000,
                FirstTokenMilliseconds: 5_000, FirstTokenSamples: 2, GenerationMilliseconds: 1_000)));
        var manager = CreateManager(llm);
        var session = manager.ActiveSession!;

        await session.SendAsync("在吗", null, CancellationToken.None);
        await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var usage = session.Snapshot.Usage!;
        Assert.Equal(1, usage.Rounds);
        Assert.Equal(2, usage.Steps);
        Assert.Equal(12.0, usage.ModelSeconds, 3);
        Assert.Equal(2.5, usage.AverageFirstTokenSeconds, 3);
        Assert.Equal(3.0, usage.TokensPerSecond, 3);

        // 累计统计随会话索引落盘，重启打开后继续可用
        var id = session.Id;
        await manager.DisposeAsync();
        _manager = null!;

        var reloaded = new ChatSessionManager(_settings, _factory, _store);
        try
        {
            var opened = await reloaded.OpenAsync(id, CancellationToken.None);
            var restored = opened.Snapshot.Usage!;
            Assert.Equal(1, restored.Rounds);
            Assert.Equal(2, restored.Steps);
            Assert.Equal(11, restored.InputTokens);
            Assert.Equal(12.0, restored.ModelSeconds, 3);
            Assert.Equal(2.5, restored.AverageFirstTokenSeconds, 3);
            Assert.Equal(3.0, restored.TokensPerSecond, 3);
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
        Assert.Equal("读取 · a.txt", tool.Tool.Label);
        Assert.Equal("读完了", messages.Last().Text);
    }

    [Fact]
    public async Task ToolLabelIsPersistedAndRestored()
    {
        var llm = new FakeLlm((_, _, _) => Streams(
            new LlmToolCallStartedEvent("call-1", "run_shell_command", "{\"command\":\"npm test\"}"),
            new LlmToolCallEvent("call-1", "run_shell_command", "{\"command\":\"npm test\"}", "ok"),
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
    public async Task ApprovalIsRoutedToCurrentSessionWithMultipleLoaded()
    {
        var llm = new FakeLlm((_, _, ct) => ApprovalStreamAsync(ct));
        var manager = CreateManager(llm);
        var first = manager.ActiveSession!;

        async IAsyncEnumerable<LlmStreamEvent> ApprovalStreamAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var approved = await _access.RequestApprovalAsync("run_shell_command", "echo two", ct);
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