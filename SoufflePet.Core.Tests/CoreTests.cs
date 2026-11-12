// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SoufflePet.Models;
using SoufflePet.Plugin.Sdk;
using SoufflePet.Services;
using SoufflePet.Utils;
using Xunit;

namespace SoufflePet.Core.Tests;

public sealed class TokenDisplayTests
{
    [Fact]
    public void FormatsCompactTokens()
    {
        Assert.Equal("999", TokenDisplay.Compact(999));
        Assert.Equal("12.5K", TokenDisplay.Compact(12_500));
        Assert.Equal("2.5M", TokenDisplay.Compact(2_500_000));
    }

    [Fact]
    public void FormatsPercent()
    {
        Assert.Equal("98.6%", TokenDisplay.Percent(0.986));
    }
}

public sealed class ChatMemoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "soufflepet-core-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void WritesReadsAndReloadsTurns()
    {
        var settings = new SouffleSettings { ChatMemoryEnabled = true };
        var memory = new ChatMemory(settings, _root);
        memory.AddTurn(ChatMemory.UserRole, "  hello  ", requestText: "hello\n[time]");
        memory.AddTurn(ChatMemory.AssistantRole, "hi", reasoning: "think",
            inputTokens: 10, outputTokens: 2, cachedInputTokens: 8, model: "p/m");

        var turns = memory.RecentTurns(10, includeTools: true);
        Assert.Equal(2, turns.Count);
        Assert.Equal("hello", turns[0].Text);
        Assert.Equal("hello\n[time]", turns[0].RequestText);
        Assert.Equal("think", turns[1].Reasoning);
        Assert.Equal(10, turns[1].InputTokens);

        var reloaded = new ChatMemory(settings, _root);
        Assert.Equal(2, reloaded.RecentTurns(10).Count);
    }

    [Fact]
    public void RemembersAndForgetsProfileEntries()
    {
        var settings = new SouffleSettings { ChatMemoryEnabled = true };
        var memory = new ChatMemory(settings, _root);
        Assert.Contains("已记住", memory.Remember("称呼", "舒芙蕾"));

        var profile = new StringBuilder();
        memory.BuildProfileBlock(profile);
        Assert.Contains("舒芙蕾", profile.ToString());

        Assert.Contains("已删除", memory.Forget("称呼"));
        var after = new StringBuilder();
        memory.BuildProfileBlock(after);
        Assert.DoesNotContain("舒芙蕾", after.ToString());
    }

    [Fact]
    public void EditsProfileEntriesFromSettingsApi()
    {
        var settings = new SouffleSettings { ChatMemoryEnabled = true };
        var memory = new ChatMemory(settings, _root);

        memory.UpsertProfileEntry("称呼", "老板");
        var entries = memory.ProfileEntries();
        Assert.Single(entries);
        Assert.Equal("老板", entries[0].Value);

        // 同键覆盖，而不是新增一条
        memory.UpsertProfileEntry("称呼", "老板娘");
        entries = memory.ProfileEntries();
        Assert.Single(entries);
        Assert.Equal("老板娘", entries[0].Value);

        // 给 previousKey 时改名：旧条目被删掉，不会留下两份
        memory.UpsertProfileEntry("昵称", "老板娘", previousKey: "称呼");
        entries = memory.ProfileEntries();
        Assert.Single(entries);
        Assert.Equal("昵称", entries[0].Key);

        Assert.True(memory.DeleteProfileEntry("昵称"));
        Assert.Empty(memory.ProfileEntries());
        Assert.False(memory.DeleteProfileEntry("昵称"));

        memory.UpsertProfileEntry("常用语言", "中文");
        memory.ClearProfile();
        Assert.Empty(memory.ProfileEntries());
    }

    [Fact]
    public void EditsProfileEvenWhenMemoryDisabled()
    {
        var settings = new SouffleSettings { ChatMemoryEnabled = false };
        var memory = new ChatMemory(settings, _root);

        // 关闭记忆只停止注入，设置页仍可查看与编辑
        memory.UpsertProfileEntry("称呼", "老板");
        Assert.Single(memory.ProfileEntries());

        var profile = new StringBuilder();
        memory.BuildProfileBlock(profile);
        Assert.DoesNotContain("老板", profile.ToString());
    }

    [Fact]
    public void SkipsCorruptJsonLines()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "conversations.jsonl"),
            "not-json" + Environment.NewLine +
            "{\"Role\":\"user\",\"Text\":\"ok\",\"Time\":\"2026-11-05T10:00:00+08:00\"}" + Environment.NewLine);

        var memory = new ChatMemory(new SouffleSettings { ChatMemoryEnabled = true }, _root);
        var turns = memory.RecentTurns(10);
        Assert.Single(turns);
        Assert.Equal("ok", turns[0].Text);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 测试清理失败不掩盖断言结果
        }
    }
}

public sealed class ScheduledTaskTests
{
    private static readonly DateTime Reference = new(2026, 11, 5, 8, 0, 0);

    [Fact]
    public void ComputesNextDaily()
    {
        var task = new ScheduledTask { Kind = ScheduledTaskKind.Daily, TimeOfDay = new TimeSpan(9, 0, 0) };
        Assert.Equal(new DateTime(2026, 11, 5, 9, 0, 0), ScheduledTaskService.ComputeNext(task, Reference));
        Assert.Equal(new DateTime(2026, 11, 6, 9, 0, 0),
            ScheduledTaskService.ComputeNext(task, new DateTime(2026, 11, 5, 10, 0, 0)));
    }

    [Fact]
    public void ComputesNextWeekly()
    {
        var task = new ScheduledTask
        {
            Kind = ScheduledTaskKind.Weekly,
            TimeOfDay = new TimeSpan(20, 0, 0),
            Weekdays = [DayOfWeek.Friday],
        };
        // 2026-11-05 是周四，下一次周五 20:00
        Assert.Equal(new DateTime(2026, 11, 6, 20, 0, 0), ScheduledTaskService.ComputeNext(task, Reference));
    }

    [Fact]
    public void ComputesNextIntervalAndOnce()
    {
        var interval = new ScheduledTask { Kind = ScheduledTaskKind.Interval, IntervalMinutes = 45 };
        Assert.Equal(Reference.AddMinutes(45), ScheduledTaskService.ComputeNext(interval, Reference));

        // 单次任务的日期由界面按本地时区写入，这里也用本地时区构造：
        // 写死 +08:00 会在 UTC 等其它时区的机器上被 LocalDateTime 换算成前一天
        var once = new ScheduledTask
        {
            Kind = ScheduledTaskKind.Once,
            OnceDate = new DateTimeOffset(new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Local)),
            TimeOfDay = new TimeSpan(9, 0, 0),
        };
        Assert.Equal(new DateTime(2026, 11, 6, 9, 0, 0), ScheduledTaskService.ComputeNext(once, Reference));
        once.Completed = true;
        Assert.Null(ScheduledTaskService.ComputeNext(once, Reference));
    }

    [Fact]
    public void PreviewTaskDoesNotMutateSource()
    {
        var original = new ScheduledTask { Name = "old", Content = "a" };
        var preview = ScheduledTaskService.PreviewTask(
            original, "new", "b", new ScheduledTaskSchedule(ScheduledTaskKind.Daily, new TimeSpan(21, 0, 0)), true);

        Assert.Equal("old", original.Name);
        Assert.Equal("new", preview.Name);
        Assert.Equal(new TimeSpan(21, 0, 0), preview.TimeOfDay);
    }
}

public sealed class SettingsSaveTests
{
    [Fact]
    public void ConcurrentSavesLeaveCompleteJson()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "SoufflePet.json");
        var settings = new SouffleSettings { PetName = "initial" };

        Parallel.For(0, 16, index =>
        {
            settings.PetName = $"pet-{index}";
            settings.Save();
        });

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(document.RootElement.TryGetProperty("PetName", out var name));
        Assert.StartsWith("pet-", name.GetString());
    }
}

public sealed class FileTokenStoreTests
{
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    [Fact]
    public void ReusesTokenForSamePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "soufflepet-token-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "a.png");
        File.WriteAllBytes(file, [1, 2, 3]);
        try
        {
            var clock = new FakeClock();
            var store = new FileTokenStore(clock, TimeSpan.FromMinutes(1));
            var first = store.RegisterFile(file, TimeSpan.FromHours(12));
            var second = store.RegisterFile(file, TimeSpan.FromHours(12));

            Assert.Equal(first, second);
            Assert.Equal(1, store.Count);
            Assert.Equal(1, store.PathCount);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ExpiresTokensAndDropsPathCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "soufflepet-token-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "b.png");
        File.WriteAllBytes(file, [1]);
        try
        {
            var clock = new FakeClock();
            var store = new FileTokenStore(clock, TimeSpan.Zero);
            var token = store.RegisterFile(file, TimeSpan.FromMinutes(5));
            Assert.NotNull(store.Take(token));

            clock.Advance(TimeSpan.FromMinutes(6));
            Assert.Null(store.Take(token));

            // 过期后再登记同一路径应拿到新 token，而不是复用已失效的那个
            var refreshed = store.RegisterFile(file, TimeSpan.FromMinutes(5));
            Assert.NotEqual(token, refreshed);
            Assert.Equal(file, store.Take(refreshed)!.FilePath);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ConsumesJsonTokensOnce()
    {
        var store = new FileTokenStore(new FakeClock(), TimeSpan.FromMinutes(1));
        var token = store.RegisterJson("{\"a\":1}", TimeSpan.FromMinutes(5));

        Assert.NotNull(store.Take(token));
        store.Consume(token);
        Assert.Null(store.Take(token));

        var file = Path.Combine(Path.GetTempPath(), $"soufflepet-token-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "x");
        try
        {
            var fileToken = store.RegisterFile(file, TimeSpan.FromMinutes(5));
            store.Consume(fileToken);
            Assert.NotNull(store.Take(fileToken)); // 文件 token 可重复读
        }
        finally
        {
            try { File.Delete(file); } catch { /* ignore */ }
        }
    }
}

public sealed class PluginManifestTests
{
    [Fact]
    public void ParsesApprovalToolsAndValidatesThem()
    {
        var manifest = PluginManifest.Parse(
            """
            {
              "id": "demo",
              "assembly": "Demo.dll",
              "approvalTools": ["mouse_click", "keyboard_type"]
            }
            """);

        Assert.Equal(["mouse_click", "keyboard_type"], manifest.ApprovalTools);
        Assert.Null(manifest.Validate());
    }

    [Fact]
    public void RejectsBlankApprovalToolName()
    {
        var manifest = PluginManifest.Parse(
            """{"id":"demo","assembly":"Demo.dll","approvalTools":[" "] }""");

        Assert.Equal("approvalTools 中存在空工具名", manifest.Validate());
    }
}