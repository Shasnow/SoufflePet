using System;
using NAudio.Wave;
using Serilog;

namespace ShufuleiPet.Services;

/// <summary>口型律动服务抽象（DI 注册用）。</summary>
public interface ILipSyncService : IDisposable
{
    /// <summary>功能开关（托盘勾选项，持久化到 autoexpression.json）：关闭时不采集。</summary>
    bool Enabled { get; set; }

    /// <summary>music 状态信号（由自动表情事件驱动）：true 表示当前处于 music 状态。</summary>
    bool Active { get; set; }

    /// <summary>平滑后的音量（0~1），驱动 ParamMouthOpenY；采集线程写、渲染/UI 线程读。</summary>
    float CurrentLevel { get; }

    /// <summary>平滑后的水平宽窄值（-1 收缩 ~ +1 扩张），驱动 ParamMouthForm2。</summary>
    float CurrentWideLevel { get; }
}

/// <summary>
/// 音乐口型律动服务：用 NAudio 的 WasapiLoopbackCapture 采集系统正在播放的声音
/// （桌面/耳机里放什么就驱动什么，无需麦克风权限），计算 RMS 音量并做攻击/释放平滑，
/// 输出 0~1 的口型值供渲染侧每帧写入 ParamMouthOpenY。
/// 采集仅在"自动表情处于 music 状态 且 托盘开关开启"时进行（App 接线），停用时不占音频设备。
/// </summary>
public sealed class LipSyncService : ILipSyncService
{
    private static readonly ILogger Logger = Log.ForContext<LipSyncService>();

    private readonly AutoExpressionConfig _config;

    private WasapiLoopbackCapture? _capture;

    // 采集线程写、UI/渲染线程读：float 单字段读写原子，无需锁
    private volatile float _level;
    private volatile float _wideLevel;

    /// <inheritdoc />
    public float CurrentLevel => _level;

    /// <inheritdoc />
    public float CurrentWideLevel => _wideLevel;

    // 宽窄双包络：快包络紧跟瞬时能量，慢包络缓慢跟随平均能量，
    // 两者差值 = 冲击感：节拍冲击时扩张（正），余音回落时收缩（负），静音回中
    private float _fastEnvelope;
    private float _slowEnvelope;

    /// <summary>实际是否正在采集（Enabled 且 Active）。</summary>
    private bool _capturing;

    /// <summary>music 状态信号（自动表情事件驱动）。</summary>
    private bool _musicActive;

    private bool _warnedFail;

    public LipSyncService(AutoExpressionConfig config)
    {
        _config = config;
    }

    /// <inheritdoc />
    public bool Enabled
    {
        get => _config.LipSyncEnabled;
        set
        {
            if (_config.LipSyncEnabled == value) return;
            _config.LipSyncEnabled = value;
            try { _config.Save(); } catch (Exception ex) { Logger.Warning(ex, "save config failed"); }
            Logger.Information("lip sync toggled enabled={Enabled}", value);
            SyncCapture();
        }
    }

    /// <inheritdoc />
    public bool Active
    {
        get => _musicActive;
        set
        {
            if (_musicActive == value) return;
            _musicActive = value;
            SyncCapture();
        }
    }

    /// <summary>按"开关 且 music 状态"决定是否采集。</summary>
    private void SyncCapture()
    {
        var want = _config.LipSyncEnabled && _musicActive;
        if (_capturing == want) return;
        _capturing = want;
        if (want) StartCapture();
        else StopCapture();
    }

    private void StartCapture()
    {
        try
        {
            // WasapiLoopbackCapture 采集默认输出设备的回环信号（系统播放的声音）
            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += OnDataAvailable;
            _capture.StartRecording();
            Logger.Information("lip sync capture started rate={Rate}", _capture.WaveFormat.SampleRate);
        }
        catch (Exception ex)
        {
            // 无输出设备/设备被独占时不抛出，仅记录一次警告并停用
            if (!_warnedFail)
            {
                _warnedFail = true;
                Logger.Warning(ex, "lip sync capture start failed, feature disabled");
            }
            StopCapture();
        }
    }

    private void StopCapture()
    {
        var capture = _capture;
        _capture = null;
        _level = 0f;      // 停用即闭嘴
        _wideLevel = 0f;  // 停用即回中
        _fastEnvelope = 0f;
        _slowEnvelope = 0f;
        if (capture == null) return;
        try
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.StopRecording();
            capture.Dispose();
            Logger.Information("lip sync capture stopped");
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "lip sync capture stop failed");
        }
    }

    /// <summary>采集回调（音频线程）：RMS 音量 → 攻击/释放平滑 → 写入 _level。</summary>
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_capture == null) return;

        // WasapiLoopbackCapture 输出 IEEE float 采样（每采样 4 字节）
        var buffer = e.Buffer;
        var count = e.BytesRecorded / 4;
        if (count == 0) return;

        var sumSquares = 0.0;
        for (var i = 0; i < count; i++)
        {
            var sample = BitConverter.ToSingle(buffer, i * 4);
            sumSquares += sample * sample;
        }
        var rms = (float)Math.Sqrt(sumSquares / count);

        // RMS → 0~1（经验值：音乐 RMS 通常在 0.05~0.3，放大 4 倍让张口幅度明显）
        var target = Math.Clamp(rms * 8f, 0f, 1f);

        // 攻击/释放平滑：上升快（0.35）跟上节奏，下降慢（0.55）避免嘴部抖动
        // var prev = _level;
        // var factor = target > prev ? 0.35f : 0.55f;
        // _level = prev + (target - prev) * factor;
        _level = target;

        // 水平宽窄：快包络（0.45，紧跟瞬时能量）- 慢包络（0.08，缓慢跟随平均能量）
        // 差值放大 3 倍并夹到 [-1,1]：节拍冲击时嘴横向扩张，余音回落时收缩，静音自动回中
        _fastEnvelope = _fastEnvelope + (target - _fastEnvelope) * 0.45f;
        _slowEnvelope = _slowEnvelope + (target - _slowEnvelope) * 0.08f;
        _wideLevel = Math.Clamp((_fastEnvelope - _slowEnvelope) * 4f, -1f, 1f);
    }

    /// <inheritdoc />
    public void Dispose() => StopCapture();
}
