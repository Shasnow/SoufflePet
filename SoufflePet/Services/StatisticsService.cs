// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using Serilog;

namespace SoufflePet.Services;

/// <summary>桌宠运行统计中的一项统计信息。</summary>
/// <param name="Key">稳定标识，供按名称查询。</param>
/// <param name="Label">中文展示名。</param>
/// <param name="Value">当前值的可读文本。</param>
public sealed record StatisticItem(string Key, string Label, string Value);

/// <summary>
/// 桌宠的运行统计（陪伴时长、对话 token 用量……），
/// 统一供界面底部信息栏与桌宠工具读取；新增统计项时在本接口与实现里各补一处即可。
/// </summary>
public interface IStatisticsService
{
    /// <summary>累计 token 用量的简洁文案（界面底部信息栏）。</summary>
    string UsageText { get; }

    /// <summary>累加一轮对话的 token 用量，并立即落盘。</summary>
    void AddUsage(long inputTokens, long outputTokens, long cachedInputTokens);

    /// <summary>累加一次「戳一戳」（用户戳了桌宠一下），并立即落盘。</summary>
    void AddPoke();

    /// <summary>累加一次「摸摸头」（用户摸了桌宠的头），并立即落盘。</summary>
    void AddHeadPat();

    /// <summary>累加一次对话次数（用户在对话窗口手输发送的轮次），并立即落盘。</summary>
    void AddConversation();

    /// <summary>全部统计项的当前快照，供统一工具与界面遍历展示。</summary>
    IReadOnlyList<StatisticItem> Snapshot();
}

/// <summary>
/// 桌宠运行统计服务：目前包含陪伴时长（跨重启累计的程序运行时间）、对话 token 用量、戳一戳次数、摸摸头次数与对话次数，
/// 统一落盘到 %AppData%\SoufflePet\statistics.json（与长期记忆同目录）。
/// 陪伴时长只累计程序运行期间的时间，用 <see cref="Stopwatch" /> 计算以免定时器漂移，
/// 每 60 秒与退出时各落一次盘，落盘值恒为「启动前累计 + 本次会话已运行」，重复落盘不会重复计数；
/// token 用量在每轮结束时即时落盘，戳一戳次数每被戳一次即时落盘，对话次数每次手输发送即时落盘。
/// </summary>
public sealed class StatisticsService : IStatisticsService, IDisposable
{
    /// <summary>陪伴时长的落盘间隔：进程被强杀时最多丢失这段时间。</summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(60);

    private static readonly ILogger Logger = Log.ForContext<StatisticsService>();

    /// <summary>保护 <see cref="_state" /> 与落盘：陪伴时长在定时器线程、token 用量在 UI 线程写入。</summary>
    private readonly Lock _gate = new();

    /// <summary>启动前已落盘的统计值；本次会话的陪伴时长不写回这里，而是计算时叠加。</summary>
    private readonly State _state;

    private readonly Stopwatch _session = Stopwatch.StartNew();
    private readonly Timer _flushTimer;
    private readonly string _path;
    private bool _disposed;

    public StatisticsService()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SoufflePet");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "statistics.json");

        _state = Load();
        _flushTimer = new Timer(_ => { if (!_disposed) Save(); }, null, FlushInterval, FlushInterval);
        Logger.Information("statistics loaded runSeconds={Seconds} usage={Usage} pokes={Pokes} headPats={HeadPats} conversations={Conversations}",
            _state.TotalRunSeconds, UsageText, _state.PokeCount, _state.HeadPatCount, _state.ConversationCount);
    }

    public long TotalRunSeconds => _state.TotalRunSeconds + (long)_session.Elapsed.TotalSeconds;

    private static string FormatRunTimeSeconds(long seconds)
    {
        var total = TimeSpan.FromSeconds(seconds);
        if (total.TotalMinutes < 1) return $"{Math.Max(0, (int)total.TotalSeconds)} 秒";
        if (total.TotalHours < 1) return $"{(int)total.TotalMinutes} 分钟";
        if (total.TotalDays < 1) return $"{(int)total.TotalHours} 小时 {total.Minutes} 分钟";
        return $"{(int)total.TotalDays} 天 {total.Hours} 小时";
    }

    public string UsageText
    {
        get
        {
            lock (_gate)
            {
                var input = _state.InputTokens;
                var total = input + _state.OutputTokens;
                var cacheRate = input > 0 ? $"{(double)_state.CachedInputTokens / input:P0}" : "—";
                return $"累计 {total:N0} tokens · 缓存命中 {cacheRate}";
            }
        }
    }

    /// <summary>戳一戳次数的可读文案；被戳在 UI 线程写、快照可能在工具线程读，所以同样走锁。</summary>
    private string PokeText
    {
        get
        {
            lock (_gate)
            {
                return $"{_state.PokeCount} 次";
            }
        }
    }

    public void AddUsage(long inputTokens, long outputTokens, long cachedInputTokens)
    {
        lock (_gate)
        {
            _state.InputTokens += inputTokens;
            _state.OutputTokens += outputTokens;
            _state.CachedInputTokens += cachedInputTokens;
            Save();
        }
    }

    public void AddPoke()
    {
        lock (_gate)
        {
            _state.PokeCount++;
            Save();
        }
    }

    /// <summary>摸摸头次数的可读文案；UI 线程写、快照可能在工具线程读，所以同样走锁。</summary>
    private string HeadPatText
    {
        get
        {
            lock (_gate)
            {
                return $"{_state.HeadPatCount} 次";
            }
        }
    }

    public void AddHeadPat()
    {
        lock (_gate)
        {
            _state.HeadPatCount++;
            Save();
        }
    }

    /// <summary>对话次数的可读文案；手输发送在 UI 线程写、快照可能在工具线程读，所以同样走锁。</summary>
    private string ConversationText
    {
        get
        {
            lock (_gate)
            {
                return $"{_state.ConversationCount} 次";
            }
        }
    }

    public void AddConversation()
    {
        lock (_gate)
        {
            _state.ConversationCount++;
            Save();
        }
    }

    public IReadOnlyList<StatisticItem> Snapshot()
    {
        var seconds = TotalRunSeconds;

        // 不足一分钟时 RunTimeText 本身就是秒数，不再重复标注「程序运行总计 N 秒」
        var runTime = FormatRunTimeSeconds(seconds);

        return
        [
            new StatisticItem("run_time", "陪伴时长", runTime),
            new StatisticItem("usage", "token 用量", UsageText),
            new StatisticItem("poke_count", "戳一戳", PokeText),
            new StatisticItem("head_pat_count", "摸摸头", HeadPatText),
            new StatisticItem("conversation_count", "对话次数", ConversationText),
        ];
    }

    private State Load()
    {
        try
        {
            if (!File.Exists(_path)) return new State();
            return JsonSerializer.Deserialize<State>(File.ReadAllText(_path)) ?? new State();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "load statistics failed");
            return new State();
        }
    }

    /// <summary>落盘：陪伴时长写「启动前累计 + 本次会话已运行」，其余统计项按当前值写。</summary>
    private void Save()
    {
        lock (_gate)
        {
            WriteStare(_path, new State
            {
                TotalRunSeconds = TotalRunSeconds,
                InputTokens = _state.InputTokens,
                OutputTokens = _state.OutputTokens,
                CachedInputTokens = _state.CachedInputTokens,
                PokeCount = _state.PokeCount,
                HeadPatCount = _state.HeadPatCount,
                ConversationCount = _state.ConversationCount,
            });
        }
    }

    private static void WriteStare(string path, State state)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(state));
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "save statistics failed");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flushTimer.Dispose();
        _session.Stop();
        Save(); // 退出前再落一次盘，尽量少丢本次会话的陪伴时长
    }

    /// <summary>落盘用的统计状态。</summary>
    private sealed class State
    {
        public long TotalRunSeconds { get; set; }

        public long InputTokens { get; set; }

        public long OutputTokens { get; set; }

        public long CachedInputTokens { get; set; }

        /// <summary>累计被戳次数（跨重启累计）。</summary>
        public long PokeCount { get; set; }

        /// <summary>累计被摸头次数（跨重启累计）。</summary>
        public long HeadPatCount { get; set; }

        /// <summary>累计对话次数（用户手输发送的轮次，跨重启累计）。</summary>
        public long ConversationCount { get; set; }
    }
}