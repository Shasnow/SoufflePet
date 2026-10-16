using System;
using NAudio.Wave;
using Serilog;
using ShufuleiPet.Models;

namespace ShufuleiPet.Services;

/// <summary>口型律动服务抽象（DI 注册用）。</summary>
public interface ILipSyncService : IDisposable
{
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
    // ==================== 口型律动可调参数 ====================

    /// <summary>自适应基准跟随速度（0~1，每帧采样步进）：越小基准越稳，嘴对整体音量变化越钝；越大基准跟得快、冲击感被削。</summary>
    private const float BaselineAdapt = 0.02f;

    /// <summary>冲击增益（倍）：比值每超出平均 1 倍口型增加的量，越大节拍冲击时张口越猛。</summary>
    private const float MouthGain = 3f;

    /// <summary>基线开度（0~1）：持续播放且响度平稳时的默认微张程度，0=平稳时全闭，1=平稳时全张。</summary>
    private const float BaselineMouth = 0.35f;

    /// <summary>张口攻击系数（0~1，每采样帧步进）：越大张口越快、越跟节拍，但过大嘴会抖。</summary>
    private const float AttackFactor = 0.5f;

    /// <summary>闭口释放系数（0~1，每采样帧步进）：越小闭口越慢越柔和，但过大（接近攻击值）会让开合对比变糊。</summary>
    private const float ReleaseFactor = 0.4f;

    /// <summary>宽窄快包络系数（0~1）：横向嘴型对口型变化的跟随速度。</summary>
    private const float WideFast = 0.45f;

    /// <summary>宽窄慢包络系数（0~1）：横向嘴型的均值参考，越小均值越稳、横向摆幅越明显。</summary>
    private const float WideSlow = 0.08f;

    /// <summary>宽窄增益（倍）：快慢包络差值的放大倍数，越大节拍时横向扩张收缩越夸张。</summary>
    private const float WideGain = 3f;

    private static readonly ILogger Logger = Log.ForContext<LipSyncService>();

    private readonly ShufuleiSettings settings;

    public LipSyncService(ShufuleiSettings settings)
    {
        this.settings = settings;
        // 开关属性变化（托盘/设置界面等任何写入方）→ 启停采集；持久化由 App 组合根统一处理
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShufuleiSettings.LipSyncEnabled)) SyncCapture();
        };
    }

    private WasapiLoopbackCapture? _capture;

    // 采集线程写、UI/渲染线程读：float 单字段读写原子，无需锁
    private volatile float _level;
    private volatile float _wideLevel;

    /// <inheritdoc />
    public float CurrentLevel => _level;

    /// <inheritdoc />
    public float CurrentWideLevel => _wideLevel;

    // 自适应包络（AGC）：
    // - _slowEnv 缓慢跟随平均响度，作为自适应基准（首帧直接取当前值避免冷启动全张嘴）
    // - 口型跟随"瞬时/平均"比值而非绝对响度：
    //   持续大音量时比值趋于 1 → 嘴保持基线微张（不再锁死 O 型），
    //   节拍冲击时比值冲高 → 大幅张口，静音间隙低于基准 → 闭嘴
    private float _slowEnv;
    private float _fastMouth;
    private float _slowMouth;

    /// <summary>实际是否正在采集（Enabled 且 Active）。</summary>
    private bool _capturing;

    /// <summary>music 状态信号（自动表情事件驱动）。</summary>
    private bool _musicActive;

    private bool _warnedFail;

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
        var want = settings.LipSyncEnabled && _musicActive;
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
        _slowEnv = 0f;
        _fastMouth = 0f;
        _slowMouth = 0f;
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

        // 自适应基准：慢包络跟随平均响度（约数秒时间常数，回调频率约 10Hz）
        if (_slowEnv <= 0f) _slowEnv = rms; // 首帧即取当前响度，避免冷启动阶段比值爆表
        else _slowEnv += (rms - _slowEnv) * BaselineAdapt;

        // 瞬时/平均比值：1=处于平均响度，>1=节拍冲击，<1=间隙
        var ratio = rms / Math.Max(_slowEnv, 1e-5f);

        // 目标口型：比值=1（持续播放的平均状态）对应 BaselineMouth 基线微张，
        // 冲击按 MouthGain 拉升，间隙降到闭嘴
        var target = Math.Clamp((ratio - 1f) * MouthGain + BaselineMouth, 0f, 1f);

        // 攻击/释放平滑：张口快跟节拍，闭口稍慢避免抖动
        var prev = _level;
        var factor = target > prev ? AttackFactor : ReleaseFactor;
        _level = prev + (target - prev) * factor;

        // 水平宽窄：快包络（跟瞬时口型）- 慢包络（口型均值）
        // 差值放大：冲击瞬间横向扩张，回落到均值以下时收缩，静音回中
        _fastMouth += (target - _fastMouth) * WideFast;
        _slowMouth += (target - _slowMouth) * WideSlow;
        _wideLevel = Math.Clamp((_fastMouth - _slowMouth) * WideGain, -1f, 1f);
    }

    /// <inheritdoc />
    public void Dispose() => StopCapture();
}
