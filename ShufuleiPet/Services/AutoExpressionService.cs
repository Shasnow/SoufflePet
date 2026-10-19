using System;
using System.Collections.Generic;
using Avalonia.Threading;
using Serilog;
using ShufuleiPet.Models;
using ShufuleiPet.Utils;

namespace ShufuleiPet.Services;

/// <summary>自动表情联动服务抽象（DI 注册用）。</summary>
public interface IAutoExpressionService : IDisposable
{
    /// <summary>检测结果变化事件：参数为要切换的表情名，null 表示恢复默认（无表情）。</summary>
    event Action<string?>? ExpressionChanged;

    /// <summary>
    /// 当前命中的自动表情名（null = 无命中）。手动模式接管期间组合根结算会忽略它——
    /// 它始终反映"原始检测结果"，不代表界面上实际显示的表情。
    /// </summary>
    string? CurrentExpression { get; }
    
    /// <summary>
    /// 当前生效的表情名（null = 无表情）。组合根结算时根据手动模式开关决定是用 CurrentExpression 还是手动模式的表情。
    /// </summary>
    string? EffectiveExpression { get; set; }
}

/// <summary>
/// 自动表情联动服务：定时按逐应用条目轮询检测（进程检测 / 前台窗口检测两种方案），
/// 命中状态汇总（game 优先于 music）后通过 ExpressionChanged 事件通知窗口切换表情，全部未命中则恢复默认。
/// 原生版与 WebView 版共用同一个服务实例（App 启动时创建一次）。
/// </summary>
public class AutoExpressionService : IAutoExpressionService
{
    private static readonly ILogger Logger = Log.ForContext<AutoExpressionService>();

    /// <summary>检测到的目标应用类别（用于状态去重，避免状态未变时重复切表情）。</summary>
    private enum AppState
    {
        None,   // 无命中
        Game,   // 命中游戏（优先级高）
        Music,  // 命中音乐
    }

    private readonly ShufuleiSettings _settings;
    private readonly DispatcherTimer _timer;

    /// <summary>
    /// 本轮实际参与检测的条目：配置文件条目 + 启动时合并的 NVIDIA App 游戏库条目。
    /// NVIDIA 条目只存在于运行期，不写回配置文件（避免把一次快照固化进用户配置）。
    /// </summary>
    private readonly List<AppEntry> _entries;

    private readonly IProcessDetector _procDetector;
    private readonly IForegroundDetector _fgDetector;

    /// <summary>本轮是否存在 foreground 条目（决定是否采集前台窗口快照）。</summary>
    private bool _hasForegroundEntries;

    /// <summary>前台条目的防抖状态（按条目索引）：连续命中/未命中的轮数与已确认状态。</summary>
    private sealed class DebounceState
    {
        public int Streak;        // raw 与上一轮相同的连续次数
        public bool LastRaw;      // 上一轮原始判定
        public bool Confirmed;    // 经防抖确认后的状态
    }
    private readonly Dictionary<int, DebounceState> _debounce = new();

    /// <summary>防抖阈值：连续 2 轮（约 6 秒）一致才确认，防 Alt-Tab 闪烁误判。</summary>
    private const int DebounceRounds = 2;

    private AppState _lastState = AppState.None;

    /// <summary>
    /// 响应 AutoExpressionEnabled 属性变化（构造时订阅 ShufuleiSettings.PropertyChanged）：
    /// 开启时启动轮询并立即检测一次；关闭时停止轮询并立即清除自动表情（无论之前处于什么状态）。
    /// 持久化由 App 组合根统一处理，这里只管运行时行为。
    /// </summary>
    private void OnEnabledChanged()
    {
        if (_settings.AutoExpressionEnabled)
        {
            _timer.Start();
            PollOnce();
        }
        else
        {
            _timer.Stop();
            _lastState = AppState.None;
            ExpressionChanged?.Invoke(null);
        }
    }

    /// <inheritdoc />
    public event Action<string?>? ExpressionChanged;

    /// <inheritdoc />
    public string? CurrentExpression => _lastState switch
    {
        AppState.Game => _settings.AutoExpressionGameExpression,
        AppState.Music => _settings.AutoExpressionMusicExpression,
        _ => null,
    };

    /// <inheritdoc />
    public string? EffectiveExpression { get; set; } = null;
    
    public AutoExpressionService(
        ShufuleiSettings settings,
        IProcessDetector processDetector,
        IForegroundDetector foregroundDetector)
    {
        _settings = settings;
        _procDetector = processDetector;
        _fgDetector = foregroundDetector;
        _entries = BuildEntries(settings);
        for (var i = 0; i < _entries.Count; i++) _entries[i].Index = i;
        _hasForegroundEntries = _entries.Exists(e => e.Method == "foreground");
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(1f, settings.AutoExpressionPollSeconds)) };
        _timer.Tick += (_, _) => PollOnce();
        // 总开关属性变化（托盘/设置界面等任何写入方）→ 启停轮询与清除表情
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShufuleiSettings.AutoExpressionEnabled)) OnEnabledChanged();
        };
        if (settings.AutoExpressionEnabled) _timer.Start();
    }

    /// <summary>
    /// 组装本轮检测条目：配置文件条目在前（用户显式配置优先），随后追加 NVIDIA App 识别为游戏、
    /// 且配置文件里没有同进程名的条目。NVIDIA 库缺失或读取失败时只返回配置文件条目。
    /// </summary>
    private static List<AppEntry> BuildEntries(ShufuleiSettings settings)
    {
        var entries = new List<AppEntry>(settings.AutoExpressionApps);
        try
        {
            var merged = 0;
            foreach (var entry in NvidiaGameLibrary.Load())
            {
                if (entries.Exists(e => string.Equals(e.Process, entry.Process, StringComparison.OrdinalIgnoreCase))) continue;
                entries.Add(entry);
                Logger.Debug("nvidia game library: merged {Process}", entry.Process);
                merged++;
            }
            if (merged > 0) Logger.Information("nvidia game library: merged {Count} game entries", merged);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "merge nvidia game library failed, ignored");
        }
        return entries;
    }

    /// <summary>轮询一次：采集快照 → 逐条目判定（含防抖）→ 汇总 → 按状态变化触发回调。</summary>
    private void PollOnce()
    {
        // 每轮各采集一次快照，条目只查表（低资源消耗）
        _procDetector.Capture();
        if (_hasForegroundEntries && _fgDetector.Supported) _fgDetector.Capture();

        var gameHit = false;
        var musicHit = false;
        foreach (var entry in _entries)
        {
            if (!Evaluate(entry)) continue; // 该条目本轮未确认命中
            if (entry.Category == "music") musicHit = true;
            else gameHit = true;
            if (gameHit && musicHit) break; // 汇总结果已定（game 优先）
        }

        var state = gameHit ? AppState.Game : musicHit ? AppState.Music : AppState.None;
        if (state == _lastState) return; // 状态未变化，不重复切表情
        var prev = _lastState;
        _lastState = state;
        var expression = state switch
        {
            AppState.Game => _settings.AutoExpressionGameExpression,
            AppState.Music => _settings.AutoExpressionMusicExpression,
            _ => null // None = 恢复默认
        };
        Logger.Information("auto expression: {Prev} -> {Next} ({Expression})", prev, state, expression ?? "default");
        ExpressionChanged?.Invoke(expression);
    }

    /// <summary>
    /// 判定单个条目本轮是否确认命中。
    /// process 条目：进程存在即命中（无防抖）。
    /// foreground 条目：标题匹配得到原始判定后，连续 DebounceRounds 轮一致才确认；
    /// 平台不支持时回退进程检测（优雅降级，不抛异常）。
    /// </summary>
    private bool Evaluate(AppEntry entry)
    {
        if (entry.Method != "foreground") return _procDetector.IsMatch(entry);

        // 非 Windows 平台：前台检测不可用，回退进程检测（优雅降级，不抛异常）
        if (!_fgDetector.Supported) return _procDetector.IsMatch(entry);

        // 三重校验（标题 + 进程）得原始判定后走防抖，未命中也要喂防抖避免残留确认状态
        return Debounce(entry, _fgDetector.IsMatch(entry));
    }

    /// <summary>前台条目防抖：raw 连续 DebounceRounds 轮一致才更新确认状态。</summary>
    private bool Debounce(AppEntry entry, bool raw)
    {
        if (!_debounce.TryGetValue(entry.Index, out var st))
        {
            st = new DebounceState();
            _debounce[entry.Index] = st;
        }

        st.Streak = raw == st.LastRaw ? st.Streak + 1 : 1;
        st.LastRaw = raw;
        if (st.Streak >= DebounceRounds && st.Confirmed != raw)
        {
            st.Confirmed = raw;
            Logger.Debug("foreground entry {Process} confirmed={Confirmed}", entry.Process, raw);
        }
        return st.Confirmed;
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
