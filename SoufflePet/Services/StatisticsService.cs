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
    /// <summary>有史以来累计运行秒数。</summary>
    long TotalRunSeconds { get; }

    /// <summary>有史以来累计运行时长。</summary>
    TimeSpan TotalRunTime { get; }

    /// <summary>时长的人类可读描述，例如「3 天 4 小时」。</summary>
    string RunTimeText { get; }

    /// <summary>累计 token 用量的简洁文案（界面底部信息栏）。</summary>
    string UsageText { get; }

    /// <summary>累加一轮对话的 token 用量，并立即落盘。</summary>
    void AddUsage(long inputTokens, long outputTokens, long cachedInputTokens);

    /// <summary>全部统计项的当前快照，供统一工具与界面遍历展示。</summary>
    IReadOnlyList<StatisticItem> Snapshot();
}

/// <summary>
/// 桌宠运行统计服务：目前包含陪伴时长（跨重启累计的程序运行时间）与对话 token 用量，
/// 统一落盘到 %AppData%\SoufflePet\statistics.json（与长期记忆同目录）。
/// 陪伴时长只累计程序运行期间的时间，用 <see cref="Stopwatch" /> 计算以免定时器漂移，
/// 每 60 秒与退出时各落一次盘，落盘值恒为「启动前累计 + 本次会话已运行」，重复落盘不会重复计数；
/// token 用量在每轮结束时即时落盘。
/// </summary>
public sealed class StatisticsService : IStatisticsService, IDisposable
{
    /// <summary>陪伴时长的落盘间隔：进程被强杀时最多丢失这段时间。</summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(60);

    private static readonly ILogger Logger = Log.ForContext<StatisticsService>();

    /// <summary>保护 <see cref="_state" /> 与落盘：陪伴时长在定时器线程、token 用量在 UI 线程写入。</summary>
    private readonly object _gate = new();

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
        Logger.Information("statistics loaded runSeconds={Seconds} usage={Usage}", _state.TotalRunSeconds, UsageText);
    }

    public long TotalRunSeconds => _state.TotalRunSeconds + (long)_session.Elapsed.TotalSeconds;

    public TimeSpan TotalRunTime => TimeSpan.FromSeconds(TotalRunSeconds);

    public string RunTimeText
    {
        get
        {
            var total = TotalRunTime;
            if (total.TotalMinutes < 1) return $"{Math.Max(0, (int)total.TotalSeconds)} 秒";
            if (total.TotalHours < 1) return $"{(int)total.TotalMinutes} 分钟";
            if (total.TotalDays < 1) return $"{(int)total.TotalHours} 小时 {total.Minutes} 分钟";
            return $"{(int)total.TotalDays} 天 {total.Hours} 小时";
        }
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

    public IReadOnlyList<StatisticItem> Snapshot()
    {
        var seconds = TotalRunSeconds;

        // 不足一分钟时 RunTimeText 本身就是秒数，不再重复标注「程序运行总计 N 秒」
        var runTime = seconds < 60 ? RunTimeText : $"{RunTimeText}（程序运行总计 {seconds:N0} 秒）";

        return
        [
            new("run_time", "陪伴时长", runTime),
            new("usage", "token 用量", UsageText),
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
            Write(_path, new State
            {
                TotalRunSeconds = TotalRunSeconds,
                InputTokens = _state.InputTokens,
                OutputTokens = _state.OutputTokens,
                CachedInputTokens = _state.CachedInputTokens,
            });
        }
    }

    private static void Write(string path, State state)
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
    }
}