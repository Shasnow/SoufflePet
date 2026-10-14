using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
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
using ShufuleiPet.Views.native;

namespace ShufuleiPet.native;

/// <summary>用 Live2DCSharpSDK（官方 Native SDK 的 C# 移植）原生渲染 Live2D 模型的 GL 控件。</summary>
public class NativePetControl : OpenGlControlBase
{
    private static readonly ILogger Logger = Log.ForContext<NativePetControl>();

    private LAppDelegateOpenGL? _lapp;
    private AvaloniaApi? _glApi;
    private LAppModel? _model;
    private string? _modelDir;
    private DateTime _last;
    private bool _inited;
    private IntPtr _hwnd;

    /// <summary>模型双眼中心的模型坐标（加载后测一次并缓存），作为目光跟随的基准点。</summary>
    private System.Numerics.Vector2? _eyeModel;
    private bool _eyeMeasured;

    /// <summary>光标下是否有模型的可见像素（每帧渲染后读取 1 像素 alpha 判定，供点击穿透用）。</summary>
    public bool IsPointerOverModel { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint lpPoint);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint lpPoint);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _hwnd = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
    }

    /// <summary>暴露给外部：切换表情（参数为 model3.json 中的表情名，如 "哭哭"）。</summary>
    public void SetExpression(string name) => _model?.SetExpression(name);

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

    /// <summary>模型加载完成前缓存的自动表情（配合 _hasPendingExpression 区分"无缓存"）。</summary>
    private string? _pendingExpression;
    private bool _hasPendingExpression;

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
                LoggingLevel = LAppDefine.CubismLoggingLevel,
            });
            Logger.Debug("CubismFramework.StartUp ok");

            _glApi = new AvaloniaApi(this, gl);
            _lapp = new PetAppDelegate(_glApi)
            {
                BGColor = new CubismTextureColor(0, 0, 0, 0) // alpha=0：透明背景，配合透明窗口做桌宠
            };
            Logger.Debug("LAppDelegateOpenGL ok");

            var modelDir = Path.Combine(AppContext.BaseDirectory, "wwwroot", "舒芙蕾");
            _modelDir = modelDir;
            _model = _lapp.Live2dManager.LoadModel(modelDir, "舒芙蕾");
            Logger.Debug("LoadModel ok textures={TextureCount}", _model.Textures.Count);

            // SDK会自动播放 Idle 动作，这里注释掉手动播放
            // PlayIdle();

            // 补设模型加载完成前缓存的自动表情（自动表情服务可能先于 GL 初始化触发）
            if (_hasPendingExpression)
            {
                _hasPendingExpression = false;
                var pending = _pendingExpression;
                _pendingExpression = null;
                if (pending == null) _model.ResetExpression();
                else _model.SetExpression(pending);
            }

            // 注意：不要设 CustomValueUpdate=true——LAppModel.Update 里它与拖拽参数
            // 应用是 if/else 互斥的，设了会禁用目光跟随。水印直接在渲染循环里隐藏。

            _last = DateTime.Now;
            Logger.Debug("OnOpenGlInit done");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "OnOpenGlInit failed");
        }
    }

    // Idle 动作循环播放（播完自动重播）
    private void PlayIdle()
    {
        if (_model == null) return;
        _model.StartMotion("Idle", 0, MotionPriority.PriorityIdle, (_, _) => PlayIdle());
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _lapp?.Dispose();
        _lapp = null;
        _model = null;
    }

    private int _frames;
    private bool _renderErrLogged;

    /// <summary>是否显示模型自带的水印层（默认关闭）。</summary>
    public bool ShowWatermark
    {
        get => _showWatermark;
        set => _showWatermark = value;
    }
    private bool _showWatermark;

    /// <summary>滚轮缩放：factor>1 放大、&lt;1 缩小，围绕窗口中心，限 0.4~3.0 倍。</summary>
    /// <remarks>
    /// 必须改 LAppView 内部的 _viewMatrix：Live2dManager.ViewMatrix
    /// 每帧都会被 LAppView.Render() 用 _viewMatrix 覆盖，直接改它无效。
    /// </remarks>
    public void Zoom(float factor)
    {
        _lapp?.View.AdjustScale(0f, 0f, factor);
    }

    /// <summary>中键拖拽：按 DIP 增量移动模型在窗口内的显示位置（窗口本身不动）。</summary>
    /// <remarks>
    /// 换算推导：投影 = Scale(h/w,1)·ViewMatrix，clip.x = (h/w)(s·mx+tx)、clip.w=1，
    /// 屏幕像素 px = (vpH·(s·mx+tx) + vpW)/2（DIP 表示同理），因此 Δtx = 2·Δpx/vpH，
    /// x、y 两轴都除以高度。AdjustTranslate 平移在裁剪空间直接叠加，与当前缩放无关。
    /// </remarks>
    public void MoveModelBy(float dx, float dy)
    {
        if (_lapp == null || Bounds.Height <= 0) return;
        var h = (float)Bounds.Height;
        _lapp.View.AdjustTranslate(2f * dx / h, -2f * dy / h);
    }

    /// <summary>托盘菜单：重置模型大小与位置（恢复初始缩放与居中位置）。</summary>
    public void ResetModel()
    {
        _lapp?.View.ResetView();
    }

    /// <summary>左键单击：随机切换一个表情。</summary>
    public void TapRandomExpression() => _model?.SetRandomExpression();

    /// <summary>
    /// 目光跟随：传入光标在窗口裁剪空间中的坐标（中心原点、y 向上，范围约 [-1,1]），
    /// 内部换算成"以模型双眼中心为基准"的视线方向后再驱动转头与眼球参数。
    /// </summary>
    /// <remarks>
    /// 基准点由模型双眼中心投影到裁剪空间得到，因此滚轮缩放、中键拖动模型后，
    /// 基准点会随模型一起移动，视线始终朝向光标。
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
    /// 测量模型双眼中心的模型坐标，作为目光跟随的基准点（只测一次并缓存）。
    /// </summary>
    /// <remarks>
    /// 眼睛部件通过 cdi3.json 的部件显示名定位（"L眼"、"R眼"），分别取其所有顶点的
    /// 均值再对两眼取平均，即"双眼之间"的中点。只测一次可避免 idle 动作使基准漂移。
    /// 找不到眼睛部件时基准点保持为视图中心，行为与改动前一致。
    /// </remarks>
    private void MeasureEyeCenter()
    {
        _eyeMeasured = true;
        var model = _model?.Model;
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
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(files[0]));
                    if (doc.RootElement.TryGetProperty("Parts", out var parts))
                    {
                        foreach (var part in parts.EnumerateArray())
                        {
                            var id = part.GetProperty("Id").GetString();
                            var name = part.GetProperty("Name").GetString();
                            if (id != null && name != null) partNames[id] = name;
                        }
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
        var eyeParts = new Dictionary<string, (double SumX, double SumY, int Drawables)>();
        unsafe
        {
            for (int i = 0; i < model.GetDrawableCount(); i++)
            {
                var partIndex = model.GetDrawableParentPartIndex(i);
                if (partIndex < 0 || partIndex >= model.PartIds.Count) continue;
                if (!partNames.TryGetValue(model.PartIds[partIndex], out var name)) continue;
                if (!name.Contains('眼') || name.Contains('镜') || name.Contains('罩') || name.Contains('泪')) continue;

                var count = model.GetDrawableVertexCount(i);
                var vertices = model.GetDrawableVertexPositions(i);
                if (count <= 0 || vertices == null) continue;

                double cx = 0, cy = 0;
                for (int k = 0; k < count; k++)
                {
                    cx += vertices[k].X;
                    cy += vertices[k].Y;
                }
                var acc = eyeParts.TryGetValue(name, out var v) ? v : (SumX: 0d, SumY: 0d, Drawables: 0);
                eyeParts[name] = (acc.SumX + cx / count, acc.SumY + cy / count, acc.Drawables + 1);
            }
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

        _eyeModel = new System.Numerics.Vector2((float)(sumX / eyeParts.Count), (float)(sumY / eyeParts.Count));
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
        => _model?.StartMotion("Idle", 0, MotionPriority.PriorityNormal, (_, _) => PlayIdle());

    /// <summary>口型律动音量（0~1）：由窗口从 ILipSyncService 每帧推送，渲染前写给模型的 LipSyncValue。</summary>
    private volatile float _lipSyncValue;

    /// <summary>口型水平宽窄值（-1 收缩 ~ +1 扩张）：驱动 ParamMouthForm。</summary>
    private volatile float _lipSyncWide;

    /// <summary>设置口型律动音量（0~1），驱动模型嘴部开合（ParamMouthOpenY）。</summary>
    public void SetLipSync(float value) => _lipSyncValue = Math.Clamp(value, 0f, 1f);

    /// <summary>设置口型水平宽窄值（-1 收缩 ~ +1 扩张），驱动嘴部横向扩张/收缩（ParamMouthForm2）。</summary>
    public void SetLipSyncWide(float value) => _lipSyncWide = Math.Clamp(value, -1f, 1f);

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_lapp == null) return;

        try
        {
            // 注意：渲染回调在渲染线程执行，此时 VisualRoot 尚未挂载，RenderScaling 取不到，
            // 模型会缩小并被锚定到窗口左下角（GL 视口原点在左下）。
            var scaling = App.RenderScaling;
            var vpW = (int)(Bounds.Width * scaling);
            var vpH = (int)(Bounds.Height * scaling);
            gl.Viewport(0, 0, vpW, vpH);

            var now = DateTime.Now;
            var span = (float)(now - _last).TotalSeconds;
            _last = now;

            // 水印是独立部件 "Part13"（cdi3.json 显示名"水印.png"），部件透明度
            // 不会被参数恢复/动作覆盖，每帧压 0 即可，等同于 VTS 空格关水印。
            if (!_showWatermark)
                _model?.Model.SetPartOpacity("Part13", 0f);

            // 口型律动：渲染前把最新音量写给模型，LAppModel.Update 的口型同步代码块
            // 会在物理演算后以 LipSyncIds 写入嘴部参数（Ids[0] 开闭，Ids[1] 水平宽窄）
            if (_model != null)
            {
                _model.LipSyncValue = _lipSyncValue;
                _model.LipSyncWideValue = _lipSyncWide;
            }

            _lapp.Run(span);

            // 顶点数据在 UpdateModel 之后才有效，故在第一帧渲染后再测量双眼中心
            if (!_eyeMeasured && _model != null) MeasureEyeCenter();

            ProbeCursorPixel(fb, vpW, vpH);
            _frames++;
            if (_frames == 1 || _frames % 300 == 0)
                Logger.Debug("render frames={Frames} span={Span:F3}", _frames, span);
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

    /// <summary>
    /// 逐像素命中检测：读取帧缓冲中光标下的 1 个像素，alpha 大于阈值说明压在模型可见区域。
    /// 背景每帧清成 (0,0,0,0)，模型像素 alpha 会累积到较高值。
    /// </summary>
    private unsafe void ProbeCursorPixel(int fb, int vpW, int vpH)
    {
        IsPointerOverModel = false;
        var api = _glApi;
        if (api == null || _lapp == null || _hwnd == IntPtr.Zero) return;
        if (!GetCursorPos(out var cur)) return;
        var origin = new NativePoint();
        if (!ClientToScreen(_hwnd, ref origin)) return;

        var px = cur.X - origin.X;
        var py = cur.Y - origin.Y;
        if (px < 0 || py < 0 || px >= vpW || py >= vpH) return; // 光标在窗口外

        var glLib = _lapp.GL;
        api.BindFramebuffer(glLib.GL_FRAMEBUFFER, fb);
        var buf = stackalloc byte[4];
        api.GLReadPixels(px, vpH - py - 1, 1, 1, glLib.GL_RGBA, glLib.GL_UNSIGNED_BYTE, buf);
        IsPointerOverModel = buf[3] >= 32;
    }

    /// <summary>
    /// 舒芙蕾模型的剪贴蒙版数超过单张渲染纹理上限（36 个），
    /// 默认 maskBufferCount=1 会触发 "not supported mask count" 异常。
    /// 这里用 3 张蒙版缓冲，上限提升到 32×3=96。
    /// </summary>
    private class PetAppDelegate(AvaloniaApi gl) : LAppDelegateOpenGL(gl)
    {
        public override CubismRenderer CreateRenderer(CubismModel model)
            => new CubismRenderer_OpenGLES2(GL, this, model, 3);
    }
}
