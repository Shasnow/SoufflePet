// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Serilog;
using SoufflePet.Core.Chat;
using SoufflePet.Models;
using SoufflePet.Services;
using SoufflePet.Services.Plugins;
using SoufflePet.Services.Web;
using SoufflePet.Utils;

namespace SoufflePet.Views.Web;

/// <summary>
/// React SPA 主窗口：唯一常驻的 WebView2 窗口，关闭只隐藏。
/// 只负责窗口外观/几何/拖拽缩放/焦点与 WebView 生命周期；业务状态全部经 WebBridgeServer。
/// </summary>
public sealed class AppWebWindow : Window, IAppWindowCommands
{
    private static readonly ILogger Logger = Log.ForContext<AppWebWindow>();

    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    private readonly ThemeService _theme;
    private readonly UiMode _mode;
    private readonly ILocalFileServer _server;
    private readonly NativeWebView _view;
    private readonly WebBridgeServer _bridge;
    private readonly ChatRpc _rpc;
    private Uri? _appOrigin;

    private bool _allowClose;
    private bool _positioned;
    private bool _hasNormalGeometry;
    private double _normalWidth = 1120;
    private double _normalHeight = 760;
    private PixelPoint _normalPosition;

    public AppWebWindow(
        SouffleSettings settings,
        ILocalFileServer files,
        ThemeService theme,
        IStatisticsService statistics,
        ChatSessionManager sessions,
        IMcpToolService mcpTools,
        IMcpConfigStore mcpConfig,
        ILlmModelCatalog catalog,
        ISkillService skills,
        IPluginService plugins,
        IScheduledTaskService scheduler,
        ILive2DModelService models,
        IUiDispatcher ui,
        UiMode mode)
    {
        _theme = theme;
        _mode = mode;
        _server = files;

        Title = $"{settings.PetName} · SoufflePet";
        Width = 1120;
        Height = 760;
        MinWidth = 400;
        MinHeight = 520;
        CanResize = true;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowDecorations = WindowDecorations.None;
        Topmost = false;
        Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1C, 0x1E));

        _view = new NativeWebView { Background = Background };
        _view.EnvironmentRequested += OnEnvironmentRequested;
        _view.NavigationStarted += OnNavigationStarted;
        _view.NewWindowRequested += OnNewWindowRequested;
        Content = _view;

        var settingsApi = new SettingsApi(
            this, settings, theme, catalog, mcpConfig, mcpTools, skills, plugins, scheduler, models, ui);
        _rpc = new ChatRpc(this, sessions, settings, theme, statistics, files, mcpTools, mcpConfig, settingsApi);
        _bridge = new WebBridgeServer(
            _view,
            files,
            _rpc);
        _bridge.PageReady += OnPageReady;
        _theme.Changed += OnThemeChanged;

        Opened += OnOpened;
        Closing += OnClosing;
        Closed += OnClosed;
        PositionChanged += (_, _) => CaptureNormalGeometry();
        SizeChanged += (_, _) => CaptureNormalGeometry();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                PublishWindowState();
                CaptureNormalGeometry();
            }
        };

        LoadPage();
    }

    /// <summary>从热键/托盘/戳一戳呼出：首次恢复几何/停靠，之后只显示并激活。</summary>
    public void ShowNear(Window anchor)
    {
        if (!_positioned)
        {
            RestoreGeometry(anchor);
            _positioned = true;
        }

        if (!IsVisible) Show();
        Activate();
        FocusInput();
        _bridge.RefreshSnapshot();
        PublishWindowState();
    }

    /// <summary>关闭按钮/热键第二次：隐藏到托盘，并记住几何。</summary>
    public void HideAndSave()
    {
        CaptureNormalGeometry();
        SaveGeometry();
        Hide();
        PublishWindowState();
    }

    public void CloseForShutdown()
    {
        _allowClose = true;
        CaptureNormalGeometry();
        SaveGeometry();
        Close();
    }

    /// <summary>聚焦输入框：WebView 的 Focus 返回值不可靠，OS 焦点实际会进容器；再让页面聚焦 composer。</summary>
    public void FocusInput()
    {
        Activate();
        _view.Focus();
        if (_bridge.IsPageReady) _ = FocusComposerAsync();
    }

    public void BeginDrag()
    {
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        ReleaseCapture();
        SendMessage(hwnd, WmNcLButtonDown, HtCaption, IntPtr.Zero);
    }

    public void BeginResize(string edge)
    {
        if (WindowState == WindowState.Maximized) return;

        var hit = edge switch
        {
            "l" => HtLeft,
            "r" => HtRight,
            "t" => HtTop,
            "b" => HtBottom,
            "lt" => HtTopLeft,
            "rt" => HtTopRight,
            "lb" => HtBottomLeft,
            "rb" => HtBottomRight,
            _ => 0,
        };
        if (hit == 0) return;

        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        if (!GetCursorPos(out var point)) return;

        ReleaseCapture();
        SendMessage(hwnd, WmNcLButtonDown, hit, MakeLParam(point.X, point.Y));
    }

    public void Minimize() => WindowState = WindowState.Minimized;

    public void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    public void HideWindow() => HideAndSave();

    /// <summary>向页面推一条 toast（M2 用于“尚未接入”的占位说明）。</summary>
    public void Notify(string text, string level = "info") =>
        _bridge.PublishEvent("toast", new { level, text });

    public WindowStateDto GetWindowState() => new(IsVisible, WindowState == WindowState.Maximized);

    public async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择附件",
                AllowMultiple = true,
            });

            return [.. files.Select(file => file.TryGetLocalPath())
                .Where(path => !string.IsNullOrEmpty(path))
                .Select(path => path!)];
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "pick attachments failed");
            return [];
        }
    }

    public async Task<string?> PickFileAsync(string title, IReadOnlyList<string> extensions)
    {
        try
        {
            var types = extensions.Count == 0
                ? null
                : new List<FilePickerFileType>
                {
                    new(title) { Patterns = [.. extensions.Select(extension => $"*{extension}")] },
                };

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = types,
            });

            return files.Count == 0 ? null : files[0].TryGetLocalPath();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "pick file failed title={Title}", title);
            return null;
        }
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
            });

            return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "pick workspace folder failed");
            return null;
        }
    }

    public async Task<ClipboardImage?> ReadClipboardImageAsync()
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return null;

            using var data = await clipboard.TryGetDataAsync();
            if (data is null) return null;

            var bitmap = await data.TryGetBitmapAsync();
            if (bitmap is null) return null;

            using var stream = new MemoryStream();
#pragma warning disable CS0618 // 12.x 推荐 BitmapEncoderOptions，但默认 PNG 编码足够；等该 API 稳定后再迁移
            bitmap.Save(stream);
#pragma warning restore CS0618
            return new ClipboardImage(stream.ToArray(), $"剪贴板-{DateTime.Now:HHmmss}.png");
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "read clipboard image failed");
            return null;
        }
    }

    private void LoadPage()
    {
        // 即使 WebDev 也启动本地服务器：页面里的 /local-file、/state 由 Vite proxy 转到这里。
        var port = _server.Start();
        var baseUrl = _mode.WebDev
            ? "http://127.0.0.1:5173/app/index.html"
            : $"http://localhost:{port}/app/index.html";
        _appOrigin = new Uri(baseUrl);
        var theme = _theme.GetTheme();
        var accent = Uri.EscapeDataString(theme.Accent);
        _view.Source = new Uri($"{baseUrl}?accent={accent}&mode={theme.Mode}");
        Logger.Information("react ui loading {Url}", _view.Source);
    }

    private void RestoreGeometry(Window anchor)
    {
        if (UiWindowStore.Load() is { } saved)
        {
            Width = Math.Max(MinWidth, saved.Width);
            Height = Math.Max(MinHeight, saved.Height);
            var position = WindowPlacement.ClampToWorkingArea(
                this,
                new PixelPoint(saved.X, saved.Y),
                Width,
                Height);
            Position = position;
            _normalPosition = position;
            _normalWidth = Width;
            _normalHeight = Height;
            _hasNormalGeometry = true;
            if (saved.Maximized) WindowState = WindowState.Maximized;
            return;
        }

        WindowPlacement.PositionNear(this, anchor, 1120, 760);
        _normalPosition = Position;
        _normalWidth = Width;
        _normalHeight = Height;
        _hasNormalGeometry = true;
    }

    private void CaptureNormalGeometry()
    {
        if (WindowState != WindowState.Normal) return;
        var width = Bounds.Width > 0 ? Bounds.Width : Width;
        var height = Bounds.Height > 0 ? Bounds.Height : Height;
        if (width <= 0 || height <= 0) return;
        _normalWidth = width;
        _normalHeight = height;
        _normalPosition = Position;
        _hasNormalGeometry = true;
    }

    private void SaveGeometry()
    {
        if (!_hasNormalGeometry) CaptureNormalGeometry();
        UiWindowStore.Save(new UiWindowState(
            _normalPosition.X,
            _normalPosition.Y,
            Math.Max(MinWidth, _normalWidth),
            Math.Max(MinHeight, _normalHeight),
            WindowState == WindowState.Maximized));
    }

    private void PublishWindowState()
    {
        try
        {
            _bridge.PublishWindowState(GetWindowState());
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "publish window state failed");
        }
    }

    private void OnThemeChanged(object? sender, ThemeDto e)
    {
        try
        {
            _bridge.PublishTheme(e);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "publish theme failed");
        }
    }

    private void OnPageReady()
    {
        // 首次 hello 后页面才有 __bridge.receive；补发窗口状态并让 composer 拿焦点。
        PublishWindowState();
        _ = InstallPageGuardsAsync();
        if (IsVisible) FocusInput();
    }

    /// <summary>
    /// 关闭页面默认右键菜单（保留输入框/可编辑区与已有文本选择，复制仍然可用）。
    /// DevTools 在 Release 已由 WebView 默认关闭；页面本身也没有远程内容，CSP 由 LocalFileServer 下发。
    /// </summary>
    private async Task InstallPageGuardsAsync()
    {
        const string script = """
            (() => {
              if (window.__souffleGuards) return;
              window.__souffleGuards = true;
              document.addEventListener('contextmenu', (event) => {
                const target = event.target;
                if (target && target.closest && target.closest('input, textarea, [contenteditable="true"]')) return;
                if (window.getSelection && String(window.getSelection())) return;
                event.preventDefault();
              });
            })();
            """;
        try
        {
            await _view.InvokeScript(script);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "install page guards failed");
        }
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        ApplyRoundedCorners();
        FocusInput();
        PublishWindowState();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose)
        {
            CaptureNormalGeometry();
            SaveGeometry();
            return;
        }

        e.Cancel = true;
        HideAndSave();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _theme.Changed -= OnThemeChanged;
        _view.NavigationStarted -= OnNavigationStarted;
        _view.NewWindowRequested -= OnNewWindowRequested;
        _bridge.PageReady -= OnPageReady;
        _bridge.Dispose();
        _rpc.Dispose();
    }

    private async Task FocusComposerAsync()
    {
        try
        {
            await _view.InvokeScript(
                "document.querySelector('textarea[aria-label=\"输入消息\"]')?.focus();");
        }
        catch
        {
            // 页面尚未就绪：等 Opened/页面自身 autofocus
        }
    }

    /// <summary>
    /// 只允许 SPA 自己所在的源（内置静态服务器或 Vite dev server）导航。
    /// 其它 http/https 链接交给系统默认浏览器；javascript:/file: 等一律拒绝。
    /// </summary>
    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (e.Request is not { } target) return;
        if (_appOrigin is not null && IsSameOrigin(target, _appOrigin)) return;

        e.Cancel = true;
        OpenExternal(target);
    }

    /// <summary>页面里的 target="_blank" / window.open 不在应用内开新窗口，外链交给系统浏览器。</summary>
    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.Request is { } target) OpenExternal(target);
    }

    private static bool IsSameOrigin(Uri target, Uri origin) =>
        target.Scheme == origin.Scheme
        && string.Equals(target.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
        && target.Port == origin.Port;

    private static void OpenExternal(Uri target)
    {
        if (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps) return;
        try
        {
            Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "open external link failed url={Url}", target);
        }
    }

    private static void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        WebViewProfile.Apply(e);
#if DEBUG
        if (e is WindowsWebView2EnvironmentRequestedEventArgs windows)
            windows.EnableDevTools = true;
#endif
    }

    /// <summary>
    /// Windows 11 的 DWM 圆角：无边框窗口默认是直角，和页面里的圆角卡片不协调。
    /// 低于 22H2 时该属性不存在，DwmSetWindowAttribute 会失败，直接忽略。
    /// </summary>
    private void ApplyRoundedCorners()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        var preference = DwmwcpRound;
        try
        {
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "apply dwm rounded corners failed");
        }
    }

    private static IntPtr MakeLParam(int x, int y) =>
        (y << 16) | (x & 0xFFFF);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }
}