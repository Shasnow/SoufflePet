using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ShufuleiPet.Views;

public partial class MainWindow : Window, IPetWindow
{
    private static readonly string WwwRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    public const int Port = 18923;

    private HttpListener? _server;
    private bool _exiting;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private const uint WmNcLButtonDown = 0x00A1; // WM_NCLBUTTONDOWN
    private static readonly IntPtr HtCaption = new(2); // HTCAPTION

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint lpPoint);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint lpPoint);

    private readonly DispatcherTimer _cursorTimer;
    private int _lastCursorX = int.MinValue;
    private int _lastCursorY = int.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        var port = StartStaticServer();
        WebView.Source = new Uri($"http://localhost:{port}/index.html");

        Opened += (_, _) => MoveToBottomRight();
        WebView.WebMessageReceived += OnWebMessage;

        // 全局鼠标追踪：页面里的 pointermove 只在光标位于窗口内时触发，移出窗口后
        // 视线会定住。这里由宿主轮询屏幕光标，换算成页面坐标后转发给页面。
        _cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _cursorTimer.Tick += (_, _) => ForwardGlobalCursor();
        _cursorTimer.Start();

        Closing += (_, e) =>
        {
            if (_exiting) return;
            e.Cancel = true;
            Hide(); // 关闭窗口 = 藏到托盘，托盘菜单才能真正退出
        };
    }

    /// <summary>托盘菜单：显示 / 隐藏切换。</summary>
    public void ToggleVisible()
    {
        if (IsVisible) Hide();
        else { Show(); Activate(); }
    }

    /// <summary>托盘菜单：重置模型大小与位置（转发给页面执行）。</summary>
    public void ResetModel()
    {
        try
        {
            _ = WebView.InvokeScript("window.onHostResetModel && window.onHostResetModel();");
        }
        catch
        {
            // 页面尚未加载完成时忽略
        }
    }

    /// <summary>自动表情联动：缓存状态后转发给页面；页面加载完成前先缓存，收到 ready 后重放。</summary>
    public void SetAutoExpression(string? name)
    {
        _autoExpression = name;
        PushAutoExpression();
    }

    /// <summary>当前自动表情状态（页面模型异步加载完成前缓存，ready 后重放）。</summary>
    private string? _autoExpression;

    /// <summary>把自动表情状态推给页面（页面未加载完成时 InvokeScript 会抛异常，忽略即可）。</summary>
    private void PushAutoExpression()
    {
        try
        {
            var script = _autoExpression == null
                ? "window.onHostResetExpression && window.onHostResetExpression();"
                : $"window.onHostSetExpression && window.onHostSetExpression('{_autoExpression}');";
            _ = WebView.InvokeScript(script);
        }
        catch
        {
            // 页面尚未加载完成时忽略，等 ready 消息重放
        }
    }

    /// <summary>托盘菜单：真正退出（解除 Closing 劫持并停掉本地服务器）。</summary>
    public void ExitForShutdown()
    {
        _exiting = true;
        try { _server?.Stop(); } catch { /* ignore */ }
        Close();
    }

    private void MoveToBottomRight()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var wa = screen.WorkingArea;
        Position = new PixelPoint(wa.Right - (int)Bounds.Width - 48, wa.Bottom - (int)Bounds.Height - 24);
    }

    /// <summary>
    /// 全局鼠标追踪：GetCursorPos 轮询屏幕光标（物理像素），用 ClientToScreen 换算成
    /// 窗口客户区原点后再除以 RenderScaling 得到页面坐标（CSS 像素），
    /// 转发给页面驱动 model.focus，光标移出窗口后视线仍继续跟随。
    /// </summary>
    private void ForwardGlobalCursor()
    {
        if (!IsVisible || _exiting) return;
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        if (!GetCursorPos(out var cursor)) return;
        if (cursor.X == _lastCursorX && cursor.Y == _lastCursorY) return;
        _lastCursorX = cursor.X;
        _lastCursorY = cursor.Y;

        var origin = new NativePoint();
        if (!ClientToScreen(hwnd, ref origin)) return;
        var scaling = RenderScaling <= 0 ? 1.0 : RenderScaling;
        var x = ((cursor.X - origin.X) / scaling).ToString(CultureInfo.InvariantCulture);
        var y = ((cursor.Y - origin.Y) / scaling).ToString(CultureInfo.InvariantCulture);
        try
        {
            // NativeWebView 没有 Post 消息 API，官方做法是 InvokeScript 执行页面 JS
            _ = WebView.InvokeScript($"window.onHostCursor && window.onHostCursor({x},{y});");
        }
        catch
        {
            // 页面尚未加载完成时忽略
        }
    }

    // 网页里按住空白处/模型时调用 invokeCSharpAction('{"type":"drag"}')，
    // 这里转成系统级窗口拖拽（WM_NCLBUTTONDOWN + HTCAPTION），拖拽全程交给 Windows 处理
    private void OnWebMessage(object? sender, WebMessageReceivedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(e.Body)) return;
            using var doc = JsonDocument.Parse(e.Body);
            if (!doc.RootElement.TryGetProperty("type", out var type)) return;
            var kind = type.GetString();
            if (kind == "ready")
            {
                // 页面模型加载完成，重放此前缓存的自动表情状态（解决启动竞态）
                PushAutoExpression();
                return;
            }
            if (kind != "drag") return;
            Dispatcher.Post(() =>
            {
                var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (hwnd == IntPtr.Zero) return;
                ReleaseCapture();
                SendMessage(hwnd, WmNcLButtonDown, HtCaption, IntPtr.Zero);
            });
        }
        catch (JsonException)
        {
            // 忽略无法解析的消息
        }
    }

    private int StartStaticServer()
    {
        for (var port = Port; port < Port + 10; port++)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException)
            {
                listener.Close();
                continue;
            }
            _server = listener;
            _ = ServeAsync(listener);
            return port;
        }
        throw new InvalidOperationException("无法启动本地静态服务器，端口 18923-18932 均被占用");
    }

    private static async Task ServeAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch
            {
                return; // listener 已停止
            }

            try
            {
                // 模型文件名是中文，URL 中被百分号编码，必须解码；根路径回退到 index.html
                var rel = Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath.TrimStart('/'));
                if (rel.Length == 0) rel = "index.html";
                var file = Path.GetFullPath(Path.Combine(WwwRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
                var root = Path.GetFullPath(WwwRoot);
                if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    continue;
                }

                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = Path.GetExtension(file).ToLowerInvariant() switch
                {
                    ".html" => "text/html; charset=utf-8",
                    ".js" => "text/javascript; charset=utf-8",
                    ".json" => "application/json; charset=utf-8",
                    ".png" => "image/png",
                    ".ico" => "image/x-icon",
                    _ => "application/octet-stream", // .moc3 等
                };
                await using (var fs = File.OpenRead(file))
                {
                    await fs.CopyToAsync(ctx.Response.OutputStream);
                }
                ctx.Response.Close();
            }
            catch
            {
                try { ctx.Response.Abort(); } catch { /* ignore */ }
            }
        }
    }
}
