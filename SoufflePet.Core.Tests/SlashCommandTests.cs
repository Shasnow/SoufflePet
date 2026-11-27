// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SoufflePet.Chat;
using SoufflePet.Core.Chat;
using SoufflePet.Models;
using SoufflePet.Services;
using Xunit;

namespace SoufflePet.Core.Tests;

/// <summary>命令的解析、目录与模糊匹配、模型解析与执行（界面补全由 React 侧负责）。</summary>
public sealed class SlashCommandTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "soufflepet-slash-tests", Guid.NewGuid().ToString("N"));

    private SouffleSettings _settings = null!;
    private ChatSessionManager _manager = null!;

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
            new FakeLlm(),
            new ChatToolAccess(_settings),
            new UserQuestionService(),
            new ChatFileService(),
            new FakeStatistics());
        _manager = new ChatSessionManager(_settings, factory, store);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
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

    [Theory]
    [InlineData("/model", "model", "", false)]
    [InlineData("/model deepseek-chat", "model", "deepseek-chat", true)]
    [InlineData("  /help", "help", "", false)]
    [InlineData("/model   ", "model", "", true)]
    [InlineData("/m DeepSeek Chat", "m", "DeepSeek Chat", true)]
    // 空白按 char.IsWhiteSpace 判定：全角空格 / NBSP / NEL 都算命令名与参数的分界，
    // 与 React 侧 slashCommands.ts 的字符集一致（否则界面认命令、宿主查无此命令）
    [InlineData("/model\u3000reason", "model", "reason", true)]
    [InlineData("/model\u00a0reason", "model", "reason", true)]
    [InlineData("/model\u0085reason", "model", "reason", true)]
    [InlineData("/model\u2003reason", "model", "reason", true)]
    // BOM 不是空白（.NET 与前端都不认），会留在命令名里 → 属于未知命令
    [InlineData("/model\ufeffreason", "model\ufeffreason", "", false)]
    public void TryParseSplitsNameAndArgument(string text, string name, string argument, bool hasSeparator)
    {
        Assert.True(SlashCommandParser.TryParse(text, out var input));
        Assert.Equal(name, input.Name);
        Assert.Equal(argument, input.Argument);
        Assert.Equal(hasSeparator, input.HasArgumentSeparator);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("普通文本")]
    [InlineData("普通 /model")]
    [InlineData("/model\n第二行")]
    public void TryParseRejectsNonCommands(string text)
    {
        Assert.False(SlashCommandParser.TryParse(text, out _));
    }

    [Fact]
    public void CatalogResolvesAliasesAndKeepsOrder()
    {
        Assert.Equal("model", SlashCommandCatalog.Find("/model")!.Name);
        Assert.Equal("model", SlashCommandCatalog.Find("m")!.Name);
        Assert.Equal("model", SlashCommandCatalog.Find("MODELS")!.Name);
        Assert.Equal("help", SlashCommandCatalog.Find("?")!.Name);
        Assert.Equal("compact", SlashCommandCatalog.Find("compress")!.Name);
        Assert.Null(SlashCommandCatalog.Find("nope"));
        Assert.Equal("model", SlashCommandCatalog.All[0].Name);
        Assert.Contains("/model [模型名称/模型ID]", SlashCommandCatalog.HelpText());
        Assert.Contains("/compact", SlashCommandCatalog.HelpText());
    }

    [Fact]
    public void FuzzyPrefersPrefixThenContainsThenSubsequence()
    {
        Assert.True(SlashCommandFuzzy.Score("model", "mod") > SlashCommandFuzzy.Score("approval", "a"));
        Assert.True(
            SlashCommandFuzzy.Score("deepseek-chat", "chat")
            > SlashCommandFuzzy.Score("deepseek-chat", "dsh"));
        Assert.Equal(SlashCommandFuzzy.NoMatch, SlashCommandFuzzy.Score("model", "zzz"));
        Assert.True(SlashCommandFuzzy.Score("deepseek-reasoner", "dsr") > SlashCommandFuzzy.NoMatch);
    }

    [Theory]
    [InlineData("DeepSeek Reasoner", "deepseek-reasoner")]
    [InlineData("deepseek-reasoner", "deepseek-reasoner")]
    [InlineData("deepseek/deepseek-reasoner", "deepseek-reasoner")]
    [InlineData("2", "deepseek-reasoner")]
    [InlineData("reasoner", "deepseek-reasoner")]
    [InlineData("DEEPSEEK CHAT", "deepseek-chat")]
    public void ResolveModelAcceptsNameIdSlotAndFuzzy(string argument, string expectedModelId)
    {
        var models = new[]
        {
            new ChatModelState("k1", "deepseek", "DeepSeek Chat", "deepseek-chat"),
            new ChatModelState("k2", "deepseek", "DeepSeek Reasoner", "deepseek-reasoner"),
        };

        var resolution = SlashCommands.ResolveModel(models, argument);
        Assert.Null(resolution.Error);
        Assert.Equal(expectedModelId, resolution.Model!.ModelId);
    }

    [Fact]
    public void ResolveModelReportsUnknownAndAmbiguous()
    {
        var models = new[]
        {
            new ChatModelState("k1", "p", "Chat A", "same"),
            new ChatModelState("k2", "p", "Chat B", "same"),
        };

        var unknown = SlashCommands.ResolveModel(models, "zzz");
        Assert.Null(unknown.Model);
        Assert.Contains("没有匹配", unknown.Error);

        var ambiguous = SlashCommands.ResolveModel(models, "chat");
        Assert.Null(ambiguous.Model);
        Assert.Contains("多个模型", ambiguous.Error);
    }

    [Fact]
    public async Task ExecuteModelSwitchesSessionAndAppendsNotice()
    {
        var session = _manager.ActiveSession!;

        var result = await SlashCommands.ExecuteAsync(session, "/model DeepSeek Reasoner");

        Assert.True(result.Success);
        Assert.Equal("model", result.CommandName);
        Assert.Equal(_settings.ChatModels[1].Key, result.ModelKey);
        Assert.Equal("DeepSeek Reasoner", session.Snapshot.Models[1].Name);
        Assert.Equal(_settings.ChatModels[1].Key, session.Snapshot.SelectedModelKey);

        var notice = session.Snapshot.Messages.Last();
        Assert.Equal(ChatMessageKinds.Notice, notice.Kind);
        Assert.Contains("已切换模型", notice.Text);
    }

    [Fact]
    public async Task ExecuteModelWithoutArgumentListsModelsAsSkipped()
    {
        var session = _manager.ActiveSession!;

        var result = await SlashCommands.ExecuteAsync(session, "/model");

        // 列出模型没有做任何改动：不是失败，界面按提示呈现（不显示「未执行」也不弹错误 toast）
        Assert.False(result.Success);
        Assert.True(result.Skipped);
        Assert.Contains("可用模型", result.Message);
        Assert.Contains("DeepSeek Chat", result.Message);
        var notice = session.Snapshot.Messages.Last();
        Assert.Equal(ChatMessageKinds.Notice, notice.Kind);
        Assert.DoesNotContain("未执行", notice.Text);
    }

    [Fact]
    public async Task ExecuteUnknownCommandAndHelpBehavePredictably()
    {
        var session = _manager.ActiveSession!;

        var unknown = await SlashCommands.ExecuteAsync(session, "/nope");
        Assert.False(unknown.Success);
        Assert.Contains("/help", unknown.Message);

        var help = await SlashCommands.ExecuteAsync(session, "/help");
        Assert.True(help.Success);
        Assert.Contains("/model", help.Message);
        Assert.Contains("/approval", help.Message);
    }

    [Fact]
    public async Task ExecuteStopFailsWhenIdleAndApprovalSwitchesMode()
    {
        var session = _manager.ActiveSession!;

        var stop = await SlashCommands.ExecuteAsync(session, "/stop");
        Assert.False(stop.Success);

        var approval = await SlashCommands.ExecuteAsync(session, "/approval full");
        Assert.True(approval.Success);
        Assert.Equal(ChatApprovalModes.Full, session.Snapshot.ApprovalMode);

        var manual = await SlashCommands.ExecuteAsync(session, "/approval manual");
        Assert.True(manual.Success);
        Assert.Equal(ChatApprovalModes.Manual, session.Snapshot.ApprovalMode);
    }

    [Fact]
    public async Task ApprovalIsRejectedWhenCommandComesFromPlatform()
    {
        var session = _manager.ActiveSession!;

        var result = await SlashCommands.ExecuteAsync(session, "/approval full", fromPlatform: true);

        Assert.False(result.Success);
        Assert.Contains("桌面端", result.Message);
        Assert.Equal(ChatApprovalModes.Manual, session.Snapshot.ApprovalMode);
        Assert.Equal(ToolApprovalMode.Manual, _settings.ToolApproval);
    }

    [Fact]
    public async Task PlatformHelpHidesDesktopOnlyCommands()
    {
        var session = _manager.ActiveSession!;

        var result = await SlashCommands.ExecuteAsync(session, "/help", fromPlatform: true);

        Assert.True(result.Success);
        Assert.Contains("/model", result.Message);
        Assert.DoesNotContain("/approval", result.Message);
        Assert.Contains("桌面端", result.Message);
    }

    [Fact]
    public async Task ExecuteNeverTouchesDraft()
    {
        var session = _manager.ActiveSession!;

        // 草稿由界面提交命令时自行清空，宿主一概不碰：
        // 长命令（/compact）期间用户输入的新内容不能被命令成功后的回调冲掉。
        await session.SetDraftAsync("/nope");
        var failed = await SlashCommands.ExecuteAsync(session, "/nope");
        Assert.False(failed.Success);
        Assert.Equal("/nope", session.Snapshot.Draft);

        await session.SetDraftAsync("/hel");
        var succeeded = await SlashCommands.ExecuteAsync(session, "/help");
        Assert.True(succeeded.Success);
        Assert.Equal("/hel", session.Snapshot.Draft);
    }

    [Fact]
    public async Task ExecuteKeepsPlainDraftUntouched()
    {
        var session = _manager.ActiveSession!;
        await session.SetDraftAsync("写了一半的草稿");

        var result = await SlashCommands.ExecuteAsync(session, "/help");

        Assert.True(result.Success);
        Assert.Equal("写了一半的草稿", session.Snapshot.Draft);
    }

    [Fact]
    public void ArgumentKindWireNamesMatchReactContract()
    {
        // 与 contracts.ts 的 SlashCommandArgumentKind 字面量一一对应
        Assert.Equal("none", SlashCommandArgumentKind.None.WireName());
        Assert.Equal("model", SlashCommandArgumentKind.Model.WireName());
        Assert.Equal("approval", SlashCommandArgumentKind.ApprovalMode.WireName());

        // 目录里每条命令都映射得出线名：新增命令 / 新增参数类型时不会漏到界面
        foreach (var command in SlashCommandCatalog.All)
            Assert.Contains(command.Argument.WireName(), new[] { "none", "model", "approval" });

        // 未定义的取值不会被静默映射成 none
        Assert.Throws<ArgumentOutOfRangeException>(() => ((SlashCommandArgumentKind)99).WireName());
    }

    private sealed class FakeLlm : ILlmChatService
    {
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            ChatModelConfig model,
            IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> history,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
            ITurnContextGuard? contextGuard = null)
        {
            await Task.Yield();
            yield return new LlmTextDelta("ok");
        }

        public Task<int> EstimateContextTokensAsync(
            ChatModelConfig model,
            IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> history,
            CancellationToken cancellationToken) =>
            Task.FromResult(SoufflePet.Services.TokenEstimator.EstimateMessages(history));

        public async IAsyncEnumerable<LlmStreamEvent> SummarizeAsync(
            ChatModelConfig model,
            IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> history,
            string instruction,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new LlmTextDelta("摘要");
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
}
