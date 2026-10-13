using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Live2DCSharpSDK.App;
using Live2DCSharpSDK.Framework;
using Live2DCSharpSDK.Framework.Motion;
using Live2DCSharpSDK.Framework.Model;
using Live2DCSharpSDK.Framework.Rendering;
using Live2DCSharpSDK.OpenGL;
using Serilog;
using ShufuleiPet.Views.native;

namespace ShufuleiPet.Views.Native;

/// <summary>用 Live2DCSharpSDK（官方 Native SDK 的 C# 移植）原生渲染 Live2D 模型的 GL 控件。</summary>
public class NativePetControl : OpenGlControlBase
{
    private static readonly ILogger Logger = Log.ForContext<NativePetControl>();

    private LAppDelegateOpenGL? _lapp;
    private AvaloniaApi? _glApi;
    private LAppModel? _model;
    private DateTime _last;
    private bool _inited;
    private IntPtr _hwnd;

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
            _model = _lapp.Live2dManager.LoadModel(modelDir, "舒芙蕾");
            Logger.Debug("LoadModel ok textures={TextureCount}", _model.Textures.Count);

            PlayIdle();

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

    /// <summary>目光跟随：传 [-1,1] 视图坐标（中心原点、y 向上），驱动转头角度和眼球参数。</summary>
    public void OnDrag(float x, float y)
    {
        _lastDragX = x; _lastDragY = y;
        _dragCalls++;
        _lapp?.Live2dManager.OnDrag(x, y);
    }
    private float _lastDragX, _lastDragY;
    private int _dragCalls;

    /// <summary>右键单击：重播 Idle 动作（该模型只带 Idle 这一个动作组）。</summary>
    public void ReplayMotion()
        => _model?.StartMotion("Idle", 0, MotionPriority.PriorityNormal, (_, _) => PlayIdle());

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_lapp == null) return;

        try
        {
            // 注意：渲染回调在渲染线程执行，此时 VisualRoot 尚未挂载，RenderScaling 取不到，
            // 兜底值必须等于本机 DPI 缩放（如 125% 屏写 1.25），否则视口小于帧缓冲，
            // 模型会缩小并被锚定到窗口左下角（GL 视口原点在左下）。
            var scaling = (VisualRoot as TopLevel)?.RenderScaling ?? 1.25;
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

            _lapp.Run(span);
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
