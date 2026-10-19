using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Live2DCSharpSDK.App;
using Live2DCSharpSDK.Framework;
using Live2DCSharpSDK.Framework.Model;
using Live2DCSharpSDK.Framework.Rendering;
using Live2DCSharpSDK.OpenGL;
using Serilog;
using ShufuleiPet.Models;
using ShufuleiPet.Services;
using ShufuleiPet.Utils;
using ShufuleiPet.Views.native;

namespace ShufuleiPet.native;

/// <summary>用 Live2DCSharpSDK（官方 Native SDK 的 C# 移植）原生渲染 Live2D 模型的 GL 控件。</summary>
public class NativePetControl(ShufuleiSettings settings) : OpenGlControlBase
{
    /// <summary>拖拽时至少保留在窗口内的可见边条（DIP），保证拖到窗口边缘外仍能抓回来。</summary>
    private const float KeepVisibleDip = 64f;

    private static readonly ILogger Logger = Log.ForContext<NativePetControl>();

    private bool _bboxMeasured;

    /// <summary>模型包围盒（视图空间，缩放 1 且未平移时测得）：中键拖拽时据此保证模型不会整体移出窗口。</summary>
    private float _bboxMinX, _bboxMaxX = 1f, _bboxMinY, _bboxMaxY = 1f;

    /// <summary>模型双眼中心的模型坐标（加载后测一次并缓存），作为目光跟随的基准点。</summary>
    private Vector2? _eyeModel;

    private int _frames;
    private AvaloniaApi? _glApi;
    private bool _hasPendingExpression;
    private bool _hasPendingGaze;
    private IntPtr _hwnd;
    private bool _inited;

    private LAppDelegateOpenGL? _lapp;

    /// <summary>上一帧的 Stopwatch 时间戳（QPC，单调且亚微秒分辨率，不受 NTP 校时拨动影响）。</summary>
    private long _lastStamp;

    /// <summary>口型律动音量（0~1）：由窗口从 ILipSyncService 每帧推送，渲染前写给模型的 LipSyncValue。</summary>
    private volatile float _lipSyncValue;

    /// <summary>口型水平宽窄值（-1 收缩 ~ +1 扩张）：驱动 ParamMouthForm。</summary>
    private volatile float _lipSyncWide;

    /// <summary>最新一帧的手柄输入快照（窗口层每帧喂入；null = 尚未接入）。</summary>
    private GamepadState? _gamepad;

    /// <summary>手柄参数驱动开关：仅在模型显示手柄（自动表情命中游戏，或托盘手动开启）时写入。</summary>
    private volatile bool _gamepadEnabled;

    /// <summary>平滑后的手柄连续量（顺序同 PadAxisKeys）：输入是 30Hz，渲染可能 180Hz，逐帧插值避免台阶感。</summary>
    private readonly float[] _padSmooth = new float[PadAxisKeys.Length];

    /// <summary>关闭驱动后是否需要把参数写回中位一次（避免摇杆残留偏转）。</summary>
    private bool _padNeedCenter = true;

    /// <summary>手柄自检日志计数（约每 3 秒打印一条写入值与模型回读值，便于确认联动生效）。</summary>
    private int _padLogTick;

    /// <summary>上一帧的时间跨度（秒），供手柄值平滑使用（渲染线程内读写，无需加锁）。</summary>
    private float _frameSpan = 1f / 60f;

    /// <summary>手柄平滑响应系数：越大越跟手（1/系数 ≈ 收敛时间常数，秒）。</summary>
    private const float PadResponse = 14f;

    /// <summary>需要平滑写入的连续量参数（cdi3.json 的 ParamGroup3「手柄」）。</summary>
    private static readonly string[] PadAxisKeys =
        ["LStickX", "LStickY", "RStickX", "RStickY", "LTrigger", "RTrigger"];

    /// <summary>直接写入 0/1 的按键参数。</summary>
    private static readonly string[] PadButtonKeys =
        ["LShoulder", "RShoulder", "LStickButton", "RStickButton", "ButtonA", "ButtonB", "ButtonX", "ButtonY"];

    private LAppModel? _model;
    private string? _modelDir;

    /// <summary>模型加载完成前缓存的自动表情（配合 _hasPendingExpression 区分"无缓存"）。</summary>
    private string? _pendingExpression;

    private bool _pendingGaze;
    private bool _renderErrLogged;

    /// <summary>是否允许自续帧（窗口隐藏时置 false，避免看不见时仍满速空转）。</summary>
    private volatile bool _renderLoopEnabled = true;

    /// <summary>渲染帧计数（渲染线程累加，窗口层每秒取走一次用于统计 FPS）。</summary>
    private int _renderedFrames;

    /// <summary>光标下是否有模型的可见像素（渲染后定期读取 1 像素 alpha 判定，供点击穿透用）。</summary>
    public bool IsPointerOverModel { get; private set; }

    /// <summary>点击穿透探测间隔（毫秒）：满帧渲染时不必每帧同步读回。</summary>
    private const long ProbeIntervalMs = 100;

    /// <summary>上次点击穿透探测的帧时间戳（Stopwatch 刻度，渲染线程独占读写，无需加锁）。</summary>
    private long _lastProbeStamp;

    /// <summary>是否显示模型自带的水印层（默认关闭）。</summary>
    private const bool ShowWatermark = false;

    /// <summary>构造：注入设置，模型目录/名称从 ShufuleiPet.json 读取（换模型不用改代码）。</summary>

    /// <summary>取走自上次调用以来渲染的帧数并清零（跨线程安全）。</summary>
    public int TakeRenderedFrames()
    {
        return Interlocked.Exchange(ref _renderedFrames, 0);
    }

    /// <summary>窗口可见性变化时启停续帧：隐藏即停，重新显示立即拉起。</summary>
    public void SetRenderLoopEnabled(bool enabled)
    {
        if (enabled) StartRenderLoop();
        else StopRenderLoop();
    }

    /// <summary>拉起渲染链：直接请求一帧，续帧链自此自行维持。</summary>
    private void StartRenderLoop()
    {
        _renderLoopEnabled = true;
        RequestNextFrameRendering();
    }

    private void StopRenderLoop()
    {
        _renderLoopEnabled = false;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _hwnd = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
    }

    /// <summary>
    /// 自动表情联动入口：传表情名切换，传 null 恢复默认（无表情）。
    /// 模型加载前先缓存：组合根在 window.Show() 之前就会应用持久化的初始状态，
    /// 此刻 GL 尚未初始化、模型为空，必须缓存到 OnOpenGlInit 里补设（否则开启手动模式重启后会停在默认表情）。
    /// </summary>
    public void SetExpression(string? name)
    {
        if (_model == null)
        {
            _pendingExpression = name;
            _hasPendingExpression = true;
            return;
        }

        if (name == null) _model.ResetExpression();
        else _model.SetExpression(name);
    }

    /// <summary>目光跟随开关：转发给模型的 EnableFaceTracking；模型加载前先缓存。</summary>
    public void SetGazeFollow(bool enabled)
    {
        if (_model == null)
        {
            _pendingGaze = enabled;
            _hasPendingGaze = true;
            return;
        }

        _model.EnableFaceTracking = enabled;
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        if (_inited) return;
        _inited = true;
        Logger.Debug("OnOpenGlInit enter");
        try
        {
            // Cubism Framework 初始化（官方 Native Samples 的标准流程）
            CubismFramework.StartUp(new LAppAllocator(), new CubismOption
            {
                LogFunction = msg => Logger.Debug("Cubism: {Message}", msg),
                LoggingLevel = LAppDefine.CubismLoggingLevel
            });
            Logger.Debug("CubismFramework.StartUp ok");

            _glApi = new AvaloniaApi(this, gl);
            _lapp = new PetAppDelegate(_glApi)
            {
                BGColor = new CubismTextureColor(0, 0, 0, 0) // alpha=0：透明背景，配合透明窗口做桌宠
            };
            Logger.Debug("LAppDelegateOpenGL ok");

            // 模型目录/名称来自设置（ShufuleiPet.json 的 ModelsDirectory / ModelName）：
            // 换模型只需把模型目录放进 wwwroot/<ModelsDirectory>/ 并改设置，无需改代码
            var modelDir = Path.Combine(AppContext.BaseDirectory, "wwwroot", settings.ModelsDirectory, settings.ModelName);
            _modelDir = modelDir;
            Logger.Debug("loading model dir={Dir} name={Name}", modelDir, settings.ModelName);
            _model = _lapp.Live2dManager.LoadModel(modelDir, settings.ModelName);
            // 16x 各向异性过滤：修复模型嘴部唇线在高 mip 层被采样成碎点/虚线的问题
            // （模型采用 4096×4096 图集，最小 artmesh 在纹理中仅 27px 宽，屏幕约 20px）。
            // 实测该项 GPU 开销约 0.7%，与 VTS 显示效果一致，保留。
            _model.Renderer?.Anisotropy = 16f;
            // 水印是独立部件 "Part13"（cdi3.json 显示名"水印.png"），部件透明度
            // 不会被参数恢复/动作覆盖，每帧压 0 即可，等同于 VTS 空格关水印。
            if (!ShowWatermark)
                _model?.Model.SetPartOpacity("Part13", 0f);
            // 手柄参数回调：在动作/表情之后、物理演算之前写入摇杆与按键（见 ApplyGamepadParams）
            _model?.GamepadValueUpdate = ApplyGamepadParams;

            // 测量双眼中心
            MeasureEyeCenter(_model?.Model);
            Logger.Debug("LoadModel ok textures={TextureCount}", _model?.Textures.Count);

            // Idle 组动作由 SDK 在 LAppModel.Update 里自动播放，无需手动触发

            // 补设模型加载完成前缓存的自动表情（组合根应用初始状态时模型还没加载）
            if (_hasPendingExpression)
            {
                _hasPendingExpression = false;
                var pending = _pendingExpression;
                _pendingExpression = null;
                if (pending == null) _model?.ResetExpression();
                else _model?.SetExpression(pending);
            }

            // 注意：不要设 CustomValueUpdate=true——LAppModel.Update 里它与拖拽参数
            // 应用是 if/else 互斥的，设了会禁用目光跟随。水印直接在渲染循环里隐藏。

            // 补设模型加载完成前缓存的目光跟随开关（托盘可能在 GL 初始化前切换）
            if (_hasPendingGaze)
            {
                _hasPendingGaze = false;
                _model?.EnableFaceTracking = _pendingGaze;
            }

            _lastStamp = Stopwatch.GetTimestamp();
            Logger.Debug("OnOpenGlInit done");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "OnOpenGlInit failed");
        }
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_lapp == null) return;

        try
        {
            // 注意：渲染回调在渲染线程执行，此时 VisualRoot 尚未挂载，RenderScaling 取不到，
            // 模型会缩小并被锚定到窗口左下角（GL 视口原点在左下）。
            var vpW = (int)(Bounds.Width * App.RenderScaling);
            var vpH = (int)(Bounds.Height * App.RenderScaling);
            gl.Viewport(0, 0, vpW, vpH);

            // 帧步长：按真实经过时间推进动作/物理/眨眼，帧率高低不影响动画速度。
            // 恢复钳位：显示器关闭/窗口隐藏期间渲染链停摆，恢复后首帧的经过时间可达
            // 数分钟，直接当步长会让动作瞬间跳完（物理/眨眼状态突变），夹回正常步长
            var now = Stopwatch.GetTimestamp();
            var span = (float)Stopwatch.GetElapsedTime(_lastStamp, now).TotalSeconds;
            _lastStamp = now;
            if (span > 0.25f) span = 1f / 60f;
            _frameSpan = span > 0f ? span : 1f / 60f; // 供手柄值平滑使用（帧率无关）

            // 口型律动：渲染前把最新音量写给模型，LAppModel.Update 的口型同步代码块
            // 会在物理演算后以 LipSyncIds 写入嘴部参数（Ids[0] 开闭，Ids[1] 水平宽窄）
            if (_model != null)
            {
                _model.LipSyncValue = _lipSyncValue;
                _model.LipSyncWideValue = _lipSyncWide;
            }

            _lapp.Run(span);

            // 点击穿透探测不随满帧渲染走：
            // glReadPixels 是同步读回，每帧都做会强制排空 GPU 管线，高刷屏下与渲染抢资源
            if (now - _lastProbeStamp >= Stopwatch.Frequency * ProbeIntervalMs / 1000)
            {
                _lastProbeStamp = now;
                ProbeCursorPixel(fb, vpW, vpH);
            }

            Interlocked.Increment(ref _renderedFrames);
            _frames++;
            if (_frames % 300 == 0)
                Logger.Debug("render frames={Frames} span={Span:F3}", _frames, span);

            // 自续帧：渲染完立刻请求下一帧，节奏交给合成器帧时钟（显示器 vsync），不限帧。
            if (_renderLoopEnabled) RequestNextFrameRendering();
        }
        catch (Exception ex)
        {
            if (!_renderErrLogged)
            {
                _renderErrLogged = true;
                Logger.Error(ex, "OnOpenGlRender exception");
            }
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _lapp?.Dispose();
        _lapp = null;
        _model = null;
    }

    /// <summary>滚轮缩放：factor>1 放大、&lt;1 缩小，围绕窗口中心，限 0.4~3.0 倍。</summary>
    /// <remarks>
    ///     必须改 LAppView 内部的 _viewMatrix：Live2dManager.ViewMatrix
    ///     每帧都会被 LAppView.Render() 用 _viewMatrix 覆盖，直接改它无效。
    /// </remarks>
    public void Zoom(float factor)
    {
        if (_lapp == null) return;
        _lapp.View.AdjustScale(0f, 0f, factor);
        // 缩放以窗口中心为轴：模型偏在一侧时放大可能把仅剩的可见部分顶出窗口，需同步钳制
        ClampModelOnScreen();
    }

    /// <summary>中键拖拽：按 DIP 增量移动模型在窗口内的显示位置（窗口本身不动）。</summary>
    /// <remarks>
    ///     换算：模型在屏幕上的位移 Δpx = 窗口高/2·Δtr（tr12/13 为视图矩阵的平移分量），故 Δtr = 2·Δpx/窗口高。
    ///     不用 AdjustTranslate：它把请求量再乘当前缩放（放大后不跟手），且其钳制窗口
    ///     随缩小急剧收缩（缩到 0.4 时仅能拖约 ±50px）。这里直接改平移分量，再按模型包围盒钳制。
    /// </remarks>
    public void MoveModelBy(float dx, float dy)
    {
        if (_lapp == null || Bounds.Height <= 0) return;
        var h = (float)Bounds.Height;
        _lapp.View.TranslateX += 2f * dx / h;
        _lapp.View.TranslateY -= 2f * dy / h;
        ClampModelOnScreen();
    }

    /// <summary>
    ///     按模型包围盒钳制拖动范围：模型缩放平移后仍要有至少 KeepVisibleDip 的边条留在窗口内。
    ///     既能把它拖到窗口的任意角落（拖出窗口一部分），又不会整个拖丢。
    /// </summary>
    /// <remarks>
    ///     模型在视图空间的包围盒 = s·[bboxMin,bboxMax] + tr（缩放以窗口中心为轴，平移为直接叠加）。
    ///     仅当模型在该轴上比窗口小时才钳制（可见边条 ≥ KeepVisibleDip）；
    ///     模型比窗口大时任意位置都不可能完全移出窗口，此时若仍钳制会因上下界倒置触发 Math.Clamp 异常。
    /// </remarks>
    private void ClampModelOnScreen()
    {
        if (_lapp == null || !_bboxMeasured || Bounds.Height <= 0) return;
        var view = _lapp.View;

        // 可见边条 DIP → 视图单位：1 视图单位 = 窗口高/2 DIP
        var k = KeepVisibleDip / ((float)Bounds.Height / 2f);
        var s = view.GetScale();
        if (s <= 0f) s = 1f;

        var loX = view.ViewLeft + k - s * _bboxMaxX;
        var hiX = view.ViewRight - k - s * _bboxMinX;
        if (loX <= hiX) view.TranslateX = Math.Clamp(view.TranslateX, loX, hiX);

        var loY = view.ViewBottom + k - s * _bboxMaxY;
        var hiY = view.ViewTop - k - s * _bboxMinY;
        if (loY <= hiY) view.TranslateY = Math.Clamp(view.TranslateY, loY, hiY);
    }

    /// <summary>托盘菜单：重置模型大小与位置（恢复初始缩放与居中位置）。</summary>
    public void ResetModel()
    {
        _lapp?.View.ResetView();
    }

    /// <summary>左键单击：随机切换一个表情。手柄显示中（自动表情命中游戏或常显手柄）不轮换，
    /// 否则点击会把手柄表情替换掉且没有任何机制切回来（手动模式下自动联动不再触发）。</summary>
    public void TapRandomExpression()
    {
        if (_gamepadEnabled) return;
        _model?.SetRandomExpression();
    }

    /// <summary>
    ///     目光跟随：传入光标在窗口裁剪空间中的坐标（中心原点、y 向上，范围约 [-1,1]），
    ///     内部换算成"以模型双眼中心为基准"的视线方向后再驱动转头与眼球参数。
    /// </summary>
    /// <remarks>
    ///     基准点由模型双眼中心投影到裁剪空间得到，因此滚轮缩放、中键拖动模型后，
    ///     基准点会随模型一起移动，视线始终朝向光标。
    /// </remarks>
    public void OnDrag(float clipX, float clipY)
    {
        var manager = _lapp?.Live2dManager;
        if (manager == null) return;

        // 基准点：模型双眼中心在裁剪空间的位置；尚未测到时退化为视图中心 (0,0)
        float baseX = 0f, baseY = 0f;
        if (_eyeModel is { } eye)
            manager.ModelToClip(eye.X, eye.Y, out baseX, out baseY);

        manager.OnDrag(
            Math.Clamp(clipX - baseX, -1f, 1f),
            Math.Clamp(clipY - baseY, -1f, 1f));
    }

    /// <summary>
    ///     测量模型双眼中心的模型坐标，作为目光跟随的基准点（只测一次并缓存）。
    /// </summary>
    /// <remarks>
    ///     眼睛部件通过 cdi3.json 的部件显示名定位（"L眼"、"R眼"），分别取其所有顶点的
    ///     均值再对两眼取平均，即"双眼之间"的中点。只测一次可避免 idle 动作使基准漂移。
    ///     找不到眼睛部件时基准点保持为视图中心，行为与改动前一致。
    /// </remarks>
    private void MeasureEyeCenter(CubismModel? model)
    {
        if (model == null) return;

        // cdi3.json 的 Parts[].{Id,Name} 提供部件 ID 到显示名的映射
        var partNames = new Dictionary<string, string>();
        try
        {
            if (_modelDir != null)
            {
                var files = Directory.GetFiles(_modelDir, "*.cdi3.json");
                if (files.Length > 0)
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(files[0]));
                    if (doc.RootElement.TryGetProperty("Parts", out var parts))
                        foreach (var part in parts.EnumerateArray())
                        {
                            var id = part.GetProperty("Id").GetString();
                            var name = part.GetProperty("Name").GetString();
                            if (id != null && name != null) partNames[id] = name;
                        }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "read cdi3.json failed");
        }

        // 眼睛部件按显示名识别：名称含"眼"即可（本模型为 "←眼"/"→眼"），
        // 但排除"眼镜"/"眼罩"/"眼泪"等干扰项。
        // 先对每个眼睛部件求顶点均值，再对这些部件中心取平均 = 双眼之间的中点。
        // 同一次顶点遍历里顺便累计模型包围盒（排除水印），供中键拖拽钳制范围。
        var eyeParts = new Dictionary<string, (double SumX, double SumY, int Drawables)>();
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        unsafe
        {
            for (var i = 0; i < model.GetDrawableCount(); i++)
            {
                var count = model.GetDrawableVertexCount(i);
                var vertices = model.GetDrawableVertexPositions(i);
                if (count <= 0 || vertices == null) continue;

                // 部件显示名：无映射时为 null
                string? name = null;
                var partIndex = model.GetDrawableParentPartIndex(i);
                if (partIndex >= 0 && partIndex < model.PartIds.Count)
                    partNames.TryGetValue(model.PartIds[partIndex], out name);

                var isWatermark = name != null && name.Contains("水印");
                var isEye = name != null && name.Contains('眼')
                                         && !name.Contains('镜') && !name.Contains('罩') && !name.Contains('泪');

                double cx = 0, cy = 0;
                for (var k = 0; k < count; k++)
                {
                    var vx = vertices[k].X;
                    var vy = vertices[k].Y;

                    // 水印部件被隐藏且几乎覆盖整个画布，不参与包围盒
                    if (!isWatermark)
                    {
                        if (vx < minX) minX = vx;
                        if (vx > maxX) maxX = vx;
                        if (vy < minY) minY = vy;
                        if (vy > maxY) maxY = vy;
                    }

                    if (isEye)
                    {
                        cx += vx;
                        cy += vy;
                    }
                }

                if (isEye)
                {
                    var acc = eyeParts.TryGetValue(name!, out var v) ? v : (SumX: 0d, SumY: 0d, Drawables: 0);
                    eyeParts[name!] = (acc.SumX + cx / count, acc.SumY + cy / count, acc.Drawables + 1);
                }
            }
        }

        // 模型包围盒由模型空间换算到视图空间（ModelMatrix 为纯缩放+平移，两角点分别变换即可）
        if (minX <= maxX && minY <= maxY && _model?.ModelMatrix is { } mm)
        {
            _bboxMinX = mm.TransformX(minX);
            _bboxMaxX = mm.TransformX(maxX);
            _bboxMinY = mm.TransformY(minY);
            _bboxMaxY = mm.TransformY(maxY);
            _bboxMeasured = true;
            Logger.Debug("model bbox view=({MinX:F4},{MinY:F4})-({MaxX:F4},{MaxY:F4})",
                _bboxMinX, _bboxMinY, _bboxMaxX, _bboxMaxY);
        }

        if (eyeParts.Count == 0)
        {
            Logger.Warning("eye parts not found, gaze falls back to view center");
            return;
        }

        double sumX = 0, sumY = 0;
        foreach (var (_, acc) in eyeParts)
        {
            sumX += acc.SumX / acc.Drawables;
            sumY += acc.SumY / acc.Drawables;
        }

        _eyeModel = new Vector2((float)(sumX / eyeParts.Count), (float)(sumY / eyeParts.Count));
        float baseX = 0f, baseY = 0f;
        if (_lapp is { } app)
            app.Live2dManager.ModelToClip(_eyeModel.Value.X, _eyeModel.Value.Y, out baseX, out baseY);
        Logger.Debug(
            "eye center model=({X:F4},{Y:F4}) clip=({BX:F4},{BY:F4}) parts={Count} canvas={W:F3}x{H:F3}",
            _eyeModel.Value.X, _eyeModel.Value.Y, baseX, baseY, eyeParts.Count,
            model.GetCanvasWidth(), model.GetCanvasHeight());
    }

    /// <summary>设置口型律动音量（0~1），驱动模型嘴部开合（ParamMouthOpenY）。</summary>
    public void SetLipSync(float value)
    {
        _lipSyncValue = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>设置口型水平宽窄值（-1 收缩 ~ +1 扩张），驱动嘴部横向扩张/收缩（ParamMouthForm2）。</summary>
    public void SetLipSyncWide(float value)
    {
        _lipSyncWide = Math.Clamp(value, -1f, 1f);
    }

    /// <summary>窗口层每帧喂入最新手柄输入（服务以 30Hz 轮询，这里只是转存引用）。</summary>
    public void SetGamepadInput(GamepadState state)
    {
        _gamepad = state;
    }

    /// <summary>
    /// 手柄参数驱动开关：仅在模型显示手柄时开启（自动表情命中游戏，或托盘手动开启手柄模式）。
    /// 关闭时会把已写入的摇杆/按键参数回中一次，避免残留偏转。
    /// </summary>
    public void SetGamepadEnabled(bool enabled)
    {
        if (_gamepadEnabled == enabled) return;
        _gamepadEnabled = enabled;
        _padNeedCenter = true;
        Logger.Information("gamepad driving enabled={Enabled}", enabled);
    }

    /// <summary>
    /// 把手柄输入写进模型参数（由 LAppModel.GamepadValueUpdate 在动作/表情之后、物理演算之前回调）。
    /// 写入的连续性：Update 开头会 LoadParameters 恢复保存值，所以必须每帧重写一次。
    /// 参数不存在时 SDK 会静默忽略（写进"不存在参数表"），因此换模型也不会抛异常。
    /// </summary>
    private void ApplyGamepadParams(LAppModel model)
    {
        var state = _gamepad;
        if (state == null) return;

        var m = model.Model;

        if (!_gamepadEnabled)
        {
            // 关闭驱动：回中一次后就不再打扰（参数由 LoadParameters 每帧自行复位）
            if (!_padNeedCenter) return;
            _padNeedCenter = false;
            for (var i = 0; i < PadAxisKeys.Length; i++)
            {
                _padSmooth[i] = 0f;
                m.SetParameterValue(PadAxisKeys[i], 0f);
            }
            foreach (var key in PadButtonKeys) m.SetParameterValue(key, 0f);
            m.SetParameterValue("DPadX", 0f);
            m.SetParameterValue("DPadY", 0f);
            return;
        }

        _padNeedCenter = true;

        // 连续量：向目标值指数逼近（帧率无关）
        var k = Math.Clamp(_frameSpan * PadResponse, 0f, 1f);
        Span<float> targets = stackalloc float[6];
        targets[0] = state.LStickX;
        targets[1] = state.LStickY;
        targets[2] = state.RStickX;
        targets[3] = state.RStickY;
        targets[4] = state.LTrigger;
        targets[5] = state.RTrigger;
        for (var i = 0; i < PadAxisKeys.Length; i++)
        {
            _padSmooth[i] += (targets[i] - _padSmooth[i]) * k;
            m.SetParameterValue(PadAxisKeys[i], _padSmooth[i]);
        }

        // 按键量：0/1 直接写（布尔动作，平滑反而糊）
        Span<float> buttons = stackalloc float[8];
        buttons[0] = Bool(state.LShoulder);
        buttons[1] = Bool(state.RShoulder);
        buttons[2] = Bool(state.LStickButton);
        buttons[3] = Bool(state.RStickButton);
        buttons[4] = Bool(state.ButtonA);
        buttons[5] = Bool(state.ButtonB);
        buttons[6] = Bool(state.ButtonX);
        buttons[7] = Bool(state.ButtonY);
        for (var i = 0; i < PadButtonKeys.Length; i++) m.SetParameterValue(PadButtonKeys[i], buttons[i]);

        m.SetParameterValue("DPadX", state.DPadX);
        m.SetParameterValue("DPadY", state.DPadY);

        // 自检日志：低频打印写入值与模型回读值，确认参数确实写进了模型（换模型/参数缺失时便于排查）
        if (++_padLogTick >= 600)
        {
            _padLogTick = 0;
            Logger.Debug(
                "gamepad write LStick=({Lx:0.00},{Ly:0.00}) RStick=({Rx:0.00},{Ry:0.00}) readback Ly={Read:0.00} src={Src}",
                _padSmooth[0], _padSmooth[1], _padSmooth[2], _padSmooth[3],
                m.GetParameterValue("LStickY"), state.HasGamepad ? "xinput" : "kb+mouse");
        }
    }

    private static float Bool(bool v) => v ? 1f : 0f;

    /// <summary>
    ///     逐像素命中检测：读取帧缓冲中光标下的 1 个像素，alpha 大于阈值说明压在模型可见区域。
    ///     背景每帧清成 (0,0,0,0)，模型像素 alpha 会累积到较高值。
    /// </summary>
    private unsafe void ProbeCursorPixel(int fb, int vpW, int vpH)
    {
        IsPointerOverModel = false;
        var api = _glApi;
        if (api == null || _lapp == null || _hwnd == IntPtr.Zero) return;
        if (!GlobalCursor.TryGetClientPos(_hwnd, out var px, out var py)) return;
        if (px < 0 || py < 0 || px >= vpW || py >= vpH) return; // 光标在窗口外

        var glLib = _lapp.GL;
        api.BindFramebuffer(glLib.GL_FRAMEBUFFER, fb);
        var buf = stackalloc byte[4];
        api.GLReadPixels(px, vpH - py - 1, 1, 1, glLib.GL_RGBA, glLib.GL_UNSIGNED_BYTE, buf);
        IsPointerOverModel = buf[3] >= 32;
    }

    /// <summary>
    ///     舒芙蕾模型的剪贴蒙版数超过单张渲染纹理上限（36 个），
    ///     默认 maskBufferCount=1 会触发 "not supported mask count" 异常。
    ///     这里用 3 张蒙版缓冲，上限提升到 32×3=96。
    /// </summary>
    private class PetAppDelegate(AvaloniaApi gl) : LAppDelegateOpenGL(gl)
    {
        public override CubismRenderer CreateRenderer(CubismModel model)
        {
            return new CubismRenderer_OpenGLES2(GL, this, model, 3);
        }
    }
}