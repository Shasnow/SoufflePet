using System;
using System.Collections.Generic;
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
using Live2DCSharpSDK.Framework.Motion;
using Live2DCSharpSDK.Framework.Rendering;
using Live2DCSharpSDK.OpenGL;
using Serilog;
using ShufuleiPet.Utils;
using ShufuleiPet.Views.native;

namespace ShufuleiPet.native;

/// <summary>用 Live2DCSharpSDK（官方 Native SDK 的 C# 移植）原生渲染 Live2D 模型的 GL 控件。</summary>
public class NativePetControl : OpenGlControlBase
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
    private DateTime _last;

    /// <summary>口型律动音量（0~1）：由窗口从 ILipSyncService 每帧推送，渲染前写给模型的 LipSyncValue。</summary>
    private volatile float _lipSyncValue;

    /// <summary>口型水平宽窄值（-1 收缩 ~ +1 扩张）：驱动 ParamMouthForm。</summary>
    private volatile float _lipSyncWide;

    private LAppModel? _model;
    private string? _modelDir;

    /// <summary>模型加载完成前缓存的自动表情（配合 _hasPendingExpression 区分"无缓存"）。</summary>
    private string? _pendingExpression;

    private bool _pendingGaze;
    private bool _renderErrLogged;

    /// <summary>是否允许自续帧（窗口隐藏时置 false，避免看不见时仍满速空转）。</summary>
    private volatile bool _renderLoopEnabled = true;

    /// <summary>帧率上限（0 = 不限制）。按 vsync 整数分频跳帧，实际帧率 = min(vsync, 上限)。</summary>
    private volatile int _fpsCap;

    /// <summary>当前显示器刷新率（窗口层查询传入；0 = 未知，退回时间阈值法）。</summary>
    private volatile int _vsyncHz;

    /// <summary>vsync 节拍计数：仅在渲染回调线程访问，每 N 拍渲染 1 帧实现整数分频。</summary>
    private int _vsyncPhase;

    /// <summary>设置帧率上限（0 表示不限制，跟随显示器 vsync）。渲染线程每帧读取。</summary>
    public void SetFpsCap(int cap) => _fpsCap = cap;

    /// <summary>设置显示器刷新率（窗口层在显示/跨屏移动时查询传入），用于 vsync 整数分频。</summary>
    public void SetVsyncHz(int hz) => _vsyncHz = hz;

    /// <summary>实际渲染帧计数（渲染线程累加，窗口层每秒取走一次用于统计 FPS）。</summary>
    private int _renderedFrames;

    /// <summary>光标下是否有模型的可见像素（每帧渲染后读取 1 像素 alpha 判定，供点击穿透用）。</summary>
    public bool IsPointerOverModel { get; private set; }

    /// <summary>是否显示模型自带的水印层（默认关闭）。</summary>
    public bool ShowWatermark { get; set; }

    /// <summary>取走自上次调用以来实际渲染的帧数并清零（跨线程安全）。</summary>
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

    /// <summary>暴露给外部：切换表情（参数为 model3.json 中的表情名，如 "哭哭"）。</summary>
    public void SetExpression(string name)
    {
        _model?.SetExpression(name);
    }

    /// <summary>自动表情联动入口：传表情名切换，传 null 恢复默认（无表情）。</summary>
    public void SetAutoExpression(string? name)
    {
        if (_model == null)
        {
            // 模型在 OnOpenGlInit 才加载，先缓存，加载完成后补设
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

            var modelDir = Path.Combine(AppContext.BaseDirectory, "wwwroot", "models", "舒芙蕾");
            _modelDir = modelDir;
            _model = _lapp.Live2dManager.LoadModel(modelDir, "舒芙蕾");
            // 16x 各向异性过滤：修复模型嘴部唇线在高 mip 层被采样成碎点/虚线的问题
            // （模型采用 4096×4096 图集，最小 artmesh 在纹理中仅 27px 宽，屏幕约 20px）。
            // 实测该项 GPU 开销约 0.7%，与 VTS 显示效果一致，保留。
            _model.Renderer?.Anisotropy = 16f;
            // 水印是独立部件 "Part13"（cdi3.json 显示名"水印.png"），部件透明度
            // 不会被参数恢复/动作覆盖，每帧压 0 即可，等同于 VTS 空格关水印。
            if (!ShowWatermark)
                _model?.Model.SetPartOpacity("Part13", 0f);
            // 测量双眼中心
            MeasureEyeCenter(_model?.Model);
            Logger.Debug("LoadModel ok textures={TextureCount}", _model?.Textures.Count);

            // SDK会自动播放 Idle 动作，这里注释掉手动播放
            // PlayIdle();

            // 补设模型加载完成前缓存的自动表情（自动表情服务可能先于 GL 初始化触发）
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

            _last = DateTime.Now;
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

        // 帧率限制：优先按 vsync 节拍整数分频——每 N 个垂直同步渲染 1 帧
        // （N = 刷新率/上限 向上取整，保证不超上限）。节拍严格均匀、相位锁定，
        // 不会像时间阈值法那样因回调触发时间的毫秒级抖动把"该画"的帧误跳掉，
        // 出现 2:1 间隔的运动拖影。上限 ≥ 刷新率时 N=1，每帧都画，与不限帧一致。
        // 刷新率未知时退回时间阈值法（留 0.5ms 容差）。跳帧不改 _last，动画速度不受影响。
        var cap = _fpsCap;
        if (cap > 0)
        {
            var hz = _vsyncHz;
            var skip = false;
            if (hz > cap)
            {
                skip = ++_vsyncPhase % (int)Math.Ceiling((double)hz / cap) != 0;
            }
            else if (hz <= 0 && (DateTime.Now - _last).TotalMilliseconds < 1000.0 / cap - 0.5)
            {
                skip = true;
            }
            if (skip)
            {
                if (_renderLoopEnabled) RequestNextFrameRendering();
                return;
            }
        }

        try
        {
            // 注意：渲染回调在渲染线程执行，此时 VisualRoot 尚未挂载，RenderScaling 取不到，
            // 模型会缩小并被锚定到窗口左下角（GL 视口原点在左下）。
            var vpW = (int)(Bounds.Width * App.RenderScaling);
            var vpH = (int)(Bounds.Height * App.RenderScaling);
            gl.Viewport(0, 0, vpW, vpH);

            var now = DateTime.Now;
            var span = (float)(now - _last).TotalSeconds;
            _last = now;

            // 口型律动：渲染前把最新音量写给模型，LAppModel.Update 的口型同步代码块
            // 会在物理演算后以 LipSyncIds 写入嘴部参数（Ids[0] 开闭，Ids[1] 水平宽窄）
            if (_model != null)
            {
                _model.LipSyncValue = _lipSyncValue;
                _model.LipSyncWideValue = _lipSyncWide;
            }

            _lapp.Run(span);

            ProbeCursorPixel(fb, vpW, vpH);

            Interlocked.Increment(ref _renderedFrames);
            _frames++;
            if (_frames == 1 || _frames % 300 == 0)
                Logger.Debug("render frames={Frames} span={Span:F3}", _frames, span);

            // 自续帧：渲染完立刻请求下一帧，节奏交给合成器帧时钟（显示器 vsync），
            // 避免固定周期定时器与高刷屏相位不同步导致的周期性掉帧。
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

    // Idle 动作循环播放（播完自动重播）
    private void PlayIdle()
    {
        _model?.StartMotion("Idle", 0, MotionPriority.PriorityIdle, (_, _) => PlayIdle());
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

    /// <summary>左键单击：随机切换一个表情。</summary>
    public void TapRandomExpression()
    {
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

    /// <summary>右键单击：重播 Idle 动作（该模型只带 Idle 这一个动作组）。</summary>
    public void ReplayMotion()
    {
        _model?.StartMotion("Idle", 0, MotionPriority.PriorityNormal, (_, _) => PlayIdle());
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