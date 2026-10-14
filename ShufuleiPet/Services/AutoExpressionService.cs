using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using Serilog;

namespace ShufuleiPet.Services;

/// <summary>单个应用的检测条目（逐应用配置检测策略）。</summary>
public class AppEntry
{
    /// <summary>目标进程名（不含 .exe，忽略大小写）。</summary>
    public string Process { get; set; } = "";

    /// <summary>类别：game（游戏）或 music（音乐），决定命中后切换的表情。</summary>
    public string Category { get; set; } = "game";

    /// <summary>检测方式：process（进程检测）或 foreground（前台窗口检测），缺省 process。</summary>
    public string Method { get; set; } = "process";

    /// <summary>
    /// 前台窗口标题匹配模式（仅 method=foreground 时生效）：/…/ 形式为正则，否则忽略大小写子串。
    /// foreground 模式只检测窗口标题，不做进程反查。
    /// </summary>
    public string TitlePattern { get; set; } = "";

    /// <summary>条目在 Apps 列表中的索引（运行时由服务赋值，供检测器缓存等按条目定位）。</summary>
    [JsonIgnore]
    internal int Index;
}

/// <summary>自动表情联动的配置（存放在可执行目录下的 autoexpression.json，可手动增删应用条目）。</summary>
public class AutoExpressionConfig
{
    /// <summary>功能总开关（托盘"自动表情"勾选项的状态）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>轮询周期（秒）：进程枚举与前台窗口查询共用同一周期。</summary>
    public float PollSeconds { get; set; } = 3f;

    /// <summary>命中 game 类条目时切换的表情名。</summary>
    public string GameExpression { get; set; } = "手柄";

    /// <summary>命中 music 类条目时切换的表情名。</summary>
    public string MusicExpression { get; set; } = "麦克风";

    /// <summary>逐应用检测条目（进程名 + 类别 + 检测方式 + 标题模式）。</summary>
    public List<AppEntry> Apps { get; set; } = BuildDefaults();

    /// <summary>
    /// 内置默认条目：全部使用进程检测。
    /// 适用场景边界——游戏全屏时无稳定标题、音乐播放器常驻后台，进程存在性判定最可靠；
    /// 需要"窗口在前台才有意义"的程序（视频播放器等）请自行改为 method=foreground 并配 titlePattern。
    /// </summary>
    public static List<AppEntry> BuildDefaults() =>
    [
        new() { Process = "steam", Category = "game", Method = "foreground", TitlePattern = "Steam" },
        new() { Process = "LeagueClient", Category = "game" },
        new() { Process = "LeagueClientUx", Category = "game" },
        new() { Process = "RiotClientServices", Category = "game" },
        new() { Process = "GenshinImpact", Category = "game" },
        new() { Process = "YuanShen", Category = "game" },
        new() { Process = "StarRail", Category = "game" },
        new() { Process = "ZenlessZoneZero", Category = "game" },
        new() { Process = "cs2", Category = "game" },
        new() { Process = "dota2", Category = "game" },
        new() { Process = "Warframe", Category = "game" },
        new() { Process = "Minecraft", Category = "game" },
        new() { Process = "QQMusic", Category = "music" },
        new() { Process = "CloudMusic", Category = "music" },
        new() { Process = "Kugou", Category = "music" },
        new() { Process = "KwMusic", Category = "music" },
        new() { Process = "spotify", Category = "music" },
        new() { Process = "foobar2000", Category = "music" },
        new() { Process = "AIMP", Category = "music" },
        new() { Process = "MusicBee", Category = "music" },
        new() { Process = "yesplaymusic", Category = "music" },
    ];

    private static readonly string ConfigPath =
        Path.Combine(AppContext.BaseDirectory, "autoexpression.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,          // 生成的模板文件方便用户手改
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 中文表情名不转义
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>旧版配置格式（gameProcesses/musicProcesses 两组白名单），仅用于读取迁移。</summary>
    private class LegacyConfig
    {
        public bool Enabled { get; set; } = true;
        public float PollSeconds { get; set; } = 3f;
        public string GameExpression { get; set; } = "手柄";
        public string MusicExpression { get; set; } = "麦克风";
        public string[]? GameProcesses { get; set; }
        public string[]? MusicProcesses { get; set; }
    }

    /// <summary>
    /// 读取配置；文件缺失或解析失败时回退到内置默认并写回模板。
    /// 检测到旧版格式（gameProcesses/musicProcesses）时自动迁移为逐应用 apps 格式并回写。
    /// </summary>
    public static AutoExpressionConfig Load()
    {
        if (!File.Exists(ConfigPath)) return WriteFallback(new AutoExpressionConfig());

        try
        {
            var text = File.ReadAllText(ConfigPath);

            // 判别格式：根含 gameProcesses/musicProcesses 即旧版，迁移为逐应用条目
            using (var doc = JsonDocument.Parse(text))
            {
                var root = doc.RootElement;
                var legacy = root.TryGetProperty("GameProcesses", out _)
                          || root.TryGetProperty("MusicProcesses", out _)
                          || root.TryGetProperty("gameProcesses", out _)
                          || root.TryGetProperty("musicProcesses", out _);
                if (legacy)
                {
                    var old = JsonSerializer.Deserialize<LegacyConfig>(text, JsonOptions);
                    if (old != null) return WriteFallback(Migrate(old));
                }
            }

            var cfg = JsonSerializer.Deserialize<AutoExpressionConfig>(text, JsonOptions);
            if (cfg != null) return cfg;
        }
        catch (Exception ex)
        {
            Log.ForContext<AutoExpressionConfig>().Warning(ex, "load autoexpression.json failed, using built-in defaults");
        }

        return WriteFallback(new AutoExpressionConfig());
    }

    /// <summary>旧格式 → 逐应用条目（数组映射类别，统一 method=process）。</summary>
    private static AutoExpressionConfig Migrate(LegacyConfig old)
    {
        var cfg = new AutoExpressionConfig
        {
            Enabled = old.Enabled,
            PollSeconds = old.PollSeconds,
            GameExpression = old.GameExpression,
            MusicExpression = old.MusicExpression,
            Apps = [],
        };
        foreach (var p in old.GameProcesses ?? [])
            cfg.Apps.Add(new AppEntry { Process = p, Category = "game" });
        foreach (var p in old.MusicProcesses ?? [])
            cfg.Apps.Add(new AppEntry { Process = p, Category = "music" });
        if (cfg.Apps.Count == 0) cfg.Apps = BuildDefaults();
        return cfg;
    }

    /// <summary>兜底：写回模板文件（失败不影响功能）并返回配置。</summary>
    private static AutoExpressionConfig WriteFallback(AutoExpressionConfig cfg)
    {
        try { cfg.Save(); } catch { /* 写模板失败不影响功能 */ }
        return cfg;
    }

    /// <summary>把当前配置写回 json 文件（开关切换时也会持久化）。</summary>
    public void Save()
    {
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}

/// <summary>
/// 自动表情联动服务：定时按逐应用条目轮询检测（进程检测 / 前台窗口检测两种方案），
/// 命中状态汇总（game 优先于 music）后通过回调通知窗口切换表情，全部未命中则恢复默认。
/// 原生版与 WebView 版共用同一个服务实例（App 启动时创建一次）。
/// </summary>
public class AutoExpressionService : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<AutoExpressionService>();

    /// <summary>检测到的目标应用类别（用于状态去重，避免状态未变时重复切表情）。</summary>
    private enum AppState
    {
        None,   // 无命中
        Game,   // 命中游戏（优先级高）
        Music,  // 命中音乐
    }

    private readonly AutoExpressionConfig _config;
    private readonly Action<string?> _onExpression; // null = 恢复默认（无表情）
    private readonly DispatcherTimer _timer;

    private readonly ProcessDetector _procDetector = new();
    private readonly ForegroundDetector _fgDetector = new();

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

    /// <summary>功能总开关：关闭时停止轮询并清除已设置的自动表情，开启时立即轮询一次。</summary>
    public bool Enabled
    {
        get => _config.Enabled;
        set
        {
            if (_config.Enabled == value) return;
            _config.Enabled = value;
            try { _config.Save(); } catch (Exception ex) { Logger.Warning(ex, "save config failed"); }
            if (value)
            {
                _timer.Start();
                PollOnce();
            }
            else
            {
                _timer.Stop();
                // 关闭开关 = 立即清除自动表情（无论之前处于什么状态）
                _lastState = AppState.None;
                _onExpression(null);
            }
        }
    }

    public AutoExpressionService(AutoExpressionConfig config, Action<string?> onExpression)
    {
        _config = config;
        _onExpression = onExpression;
        for (var i = 0; i < config.Apps.Count; i++) config.Apps[i].Index = i;
        _hasForegroundEntries = config.Apps.Exists(e => e.Method == "foreground");
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(1f, config.PollSeconds)) };
        _timer.Tick += (_, _) => PollOnce();
        if (config.Enabled) _timer.Start();
    }

    /// <summary>轮询一次：采集快照 → 逐条目判定（含防抖）→ 汇总 → 按状态变化触发回调。</summary>
    private void PollOnce()
    {
        // 每轮各采集一次快照，条目只查表（低资源消耗）
        _procDetector.Capture();
        if (_hasForegroundEntries && _fgDetector.Supported) _fgDetector.Capture();

        var gameHit = false;
        var musicHit = false;
        foreach (var entry in _config.Apps)
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
            AppState.Game => _config.GameExpression,
            AppState.Music => _config.MusicExpression,
            _ => null, // None = 恢复默认
        };
        Logger.Information("auto expression: {Prev} -> {Next} ({Expression})", prev, state, expression ?? "default");
        _onExpression(expression);
    }

    /// <summary>
    /// 判定单个条目本轮是否确认命中。
    /// process 条目：进程存在即命中（无防抖）。
    /// foreground 条目：三重校验（标题 + 进程）得到原始判定后，连续 DebounceRounds 轮一致才确认；
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
