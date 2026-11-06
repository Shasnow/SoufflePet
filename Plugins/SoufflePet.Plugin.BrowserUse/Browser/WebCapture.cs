// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace SoufflePet.Plugins.BrowserUse.Browser;

/// <summary>浏览器窗口截图结果：回传模型的压缩图片、落盘路径与原始像素尺寸。</summary>
internal sealed record BrowserScreenshot(
    byte[] Image,
    string MediaType,
    string FilePath,
    int Width,
    int Height);

/// <summary>
/// 按窗口标题截取浏览器画面（PrintWindow，可截到被遮挡或被 DWM 合成的内容），
/// 同时落盘完整 PNG，回传给模型的是缩小后的 JPEG。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WebCapture
{
    private const uint PrintWindowRenderFullContent = 0x00000002;

    /// <summary>回传给模型的图片最长边：整窗像素会按约 1 token/字符消耗上下文，必须先缩小。</summary>
    private const int ModelImageMaxEdge = 1280;

    /// <summary>回传图片的 JPEG 质量。</summary>
    private const long ModelImageQuality = 75L;

    /// <summary>回传图片的体积上限（字节）；超过时降低质量重新编码。</summary>
    private const int ModelImageMaxBytes = 250_000;

    private const int DwmwaExtendedFrameBounds = 9;

    private const int DwmwaCloaked = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hDc, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeRect(IntPtr hWnd, int attribute, out NativeRect value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt(IntPtr hWnd, int attribute, out int value, int size);

    /// <summary>按标题（可不完整、不区分大小写）截取一个窗口；被遮挡也能截到。</summary>
    public static BrowserScreenshot CaptureWindow(string title)
    {
        var target = ResolveWindow(title);
        if (target.IsMinimized)
            throw new InvalidOperationException($"窗口「{target.Title}」已最小化，请先还原再截图。");

        // GetWindowRect 含不可见的缩放边框，PrintWindow 会把它一起渲染进来，最后再裁掉
        if (!GetWindowRect(target.Handle, out var windowRect))
            throw new InvalidOperationException($"无法获取窗口「{target.Title}」的位置。");

        var width = windowRect.Right - windowRect.Left;
        var height = windowRect.Bottom - windowRect.Top;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException($"窗口「{target.Title}」尺寸为 {width}×{height}，无法截图。");

        using var full = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(full))
        {
            var hdc = graphics.GetHdc();
            try
            {
                // 先带 PW_RENDERFULLCONTENT；个别老程序不支持这个标志，退回普通模式
                if (!PrintWindow(target.Handle, hdc, PrintWindowRenderFullContent)
                    && !PrintWindow(target.Handle, hdc, 0))
                    throw new InvalidOperationException($"截取窗口「{target.Title}」失败：PrintWindow 返回 0。");
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }

        var (trimmed, _, _) = TrimInvisibleBorder(full, target.Handle, windowRect);
        using (trimmed) return BuildResult(trimmed);
    }

    /// <summary>按标题找窗口：优先完全匹配；只写一部分时要求唯一，否则报错并列出候选。</summary>
    private static WindowTarget ResolveWindow(string title)
    {
        title = title.Trim();
        var windows = GetWindows();
        var exact = windows.Where(window => string.Equals(window.Title, title, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return exact[0];

        var matches = windows.Where(window => window.Title.Contains(title, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"没有找到标题包含「{title}」的窗口。"),
            _ => throw new InvalidOperationException($"标题包含「{title}」的窗口有 {matches.Count} 个，请给出更完整的标题。")
        };
    }

    private static IReadOnlyList<WindowTarget> GetWindows()
    {
        var windows = new List<WindowTarget>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || IsCloaked(handle)) return true;

            var length = GetWindowTextLengthW(handle);
            if (length <= 0) return true;

            var builder = new StringBuilder(length + 1);
            if (GetWindowTextW(handle, builder, builder.Capacity) <= 0) return true;

            GetWindowThreadProcessId(handle, out var processId);
            windows.Add(new WindowTarget(handle, builder.ToString(), GetProcessName(processId), IsIconic(handle)));
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    /// <summary>
    /// 裁掉 Win10/11 窗口四周那圈不可见的缩放边框，只保留实际可见的画面。
    /// </summary>
    private static (Bitmap Bitmap, int Left, int Top) TrimInvisibleBorder(
        Bitmap full, IntPtr handle, NativeRect windowRect)
    {
        var windowOrigin = (windowRect.Left, windowRect.Top);
        if (DwmGetWindowAttributeRect(handle, DwmwaExtendedFrameBounds, out var visible, Marshal.SizeOf<NativeRect>()) != 0)
            return (new Bitmap(full), windowOrigin.Left, windowOrigin.Top);

        // 可见区域不可能比窗口本身大；对不上说明两组坐标的 DPI 空间不一致，此时不裁，免得误切内容
        if (visible.Right - visible.Left > full.Width || visible.Bottom - visible.Top > full.Height)
            return (new Bitmap(full), windowOrigin.Left, windowOrigin.Top);

        var x = visible.Left - windowRect.Left;
        var y = visible.Top - windowRect.Top;
        var width = Math.Min(visible.Right - visible.Left, full.Width - x);
        var height = Math.Min(visible.Bottom - visible.Top, full.Height - y);
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || (x == 0 && y == 0 && width == full.Width && height == full.Height))
            return (new Bitmap(full), windowOrigin.Left, windowOrigin.Top);

        return (full.Clone(new Rectangle(x, y, width, height), full.PixelFormat), visible.Left, visible.Top);
    }

    private static bool IsCloaked(IntPtr handle) =>
        DwmGetWindowAttributeInt(handle, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static string GetProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            // 进程已退出或无权限访问，留空即可
            return string.Empty;
        }
    }

    /// <summary>落盘完整 PNG，并生成回传模型的压缩图片。</summary>
    private static BrowserScreenshot BuildResult(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        var png = stream.ToArray();

        var directory = Path.Combine(Path.GetTempPath(), "SoufflePet", "Screenshots");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"browser-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        File.WriteAllBytes(path, png);

        var (image, mediaType) = EncodeForModel(bitmap);
        return new BrowserScreenshot(image, mediaType, path, bitmap.Width, bitmap.Height);
    }

    /// <summary>
    /// 生成回传给模型的图片：缩到最长边 <see cref="ModelImageMaxEdge" /> 并编码为 JPEG。
    /// 整窗 PNG 常有数百 KB，base64 后在按字符计 token 的上游会直接吃掉几十万上下文。
    /// </summary>
    private static (byte[] Data, string MediaType) EncodeForModel(Bitmap source)
    {
        var scale = Math.Min(1d, (double)ModelImageMaxEdge / Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var resized = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(resized))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, 0, 0, width, height);
        }

        var data = EncodeJpeg(resized, ModelImageQuality);
        if (data.Length > ModelImageMaxBytes) data = EncodeJpeg(resized, 55L);
        return (data, "image/jpeg");
    }

    private static byte[] EncodeJpeg(Image image, long quality)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        using var stream = new MemoryStream();
        image.Save(stream, codec, parameters);
        return stream.ToArray();
    }

    private sealed record WindowTarget(IntPtr Handle, string Title, string ProcessName, bool IsMinimized);
}
