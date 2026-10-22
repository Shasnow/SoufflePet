// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SoufflePet.Utils;

/// <summary>
/// 一次显示器截图的结果：<see cref="Image" /> 是压缩后回传给模型的数据（<see cref="MediaType" /> 标明格式），
/// <see cref="FilePath" /> 指向落盘的完整 PNG，<see cref="Width" />/<see cref="Height" /> 是原始分辨率。
/// </summary>
public sealed record MonitorScreenshot(byte[] Image, string MediaType, string FilePath, int Width, int Height);

/// <summary>
/// 当前显示器截图：按光标所在屏幕确定目标显示器，用 GDI+ 拷贝屏幕像素并编码为 PNG，
/// 同时写入临时目录，方便用户直接打开查看；回传给模型的则是缩小后的 JPEG。
/// </summary>
[SupportedOSPlatform("windows")]
public static class ScreenCapture
{
    private const uint MonitorDefaultToNearest = 2;

    /// <summary>回传给模型的图片最长边：整屏像素会按约 1 token/字符消耗上下文，必须先缩小。</summary>
    private const int ModelImageMaxEdge = 1280;

    /// <summary>回传图片的 JPEG 质量。</summary>
    private const long ModelImageQuality = 75L;

    /// <summary>回传图片的体积上限（字节）；超过时降低质量重新编码。</summary>
    private const int ModelImageMaxBytes = 250_000;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr hDestDc, int x, int y, int width, int height, IntPtr hSrcDc, int srcX, int srcY, uint rop);

    /// <summary>SRCCOPY：直接拷贝源像素。</summary>
    private const uint SourceCopy = 0x00CC0020;

    /// <summary>CAPTUREBLT：把分层窗口（含部分透明窗口）也纳入截图。</summary>
    private const uint CaptureBlt = 0x40000000;

    /// <summary>截取光标所在显示器，返回 PNG 数据并保存到临时目录。</summary>
    public static MonitorScreenshot CaptureCurrentMonitor()
    {
        var bounds = GetCurrentMonitorBounds();

        // 用 BitBlt 而不是 Graphics.CopyFromScreen：后者会把枚举值连同 0x40000000 一起
        // 传给参数校验，抛 InvalidEnumArgumentException 导致截图失败。
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var targetDc = graphics.GetHdc();
            var screenDc = GetDC(IntPtr.Zero);
            try
            {
                if (!BitBlt(
                        targetDc, 0, 0, bounds.Width, bounds.Height,
                        screenDc, bounds.Left, bounds.Top, SourceCopy | CaptureBlt))
                    throw new InvalidOperationException("截取屏幕失败：BitBlt 返回 0。");
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
                graphics.ReleaseHdc(targetDc);
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        var png = stream.ToArray();

        var directory = Path.Combine(Path.GetTempPath(), "SoufflePet", "Screenshots");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        File.WriteAllBytes(path, png);

        var (image, mediaType) = EncodeForModel(bitmap);
        return new MonitorScreenshot(image, mediaType, path, bounds.Width, bounds.Height);
    }

    /// <summary>
    /// 生成回传给模型的图片：缩到最长边 <see cref="ModelImageMaxEdge" /> 并编码为 JPEG。
    /// 屏幕截图的 PNG 常有数百 KB，base64 后在按字符计 token 的上游会直接吃掉几十万上下文。
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
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        using var stream = new MemoryStream();
        image.Save(stream, codec, parameters);
        return stream.ToArray();
    }

    private static Rectangle GetCurrentMonitorBounds()
    {
        // 取不到光标或显示器信息时退回主显示器
        if (GetCursorPos(out var cursor))
        {
            var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor != IntPtr.Zero && GetMonitorInfoW(monitor, ref info))
            {
                var rect = info.Monitor;
                return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            }
        }

        return new Rectangle(
            0,
            0,
            (int)GetSystemMetrics(SystemMetricCxScreen),
            (int)GetSystemMetrics(SystemMetricCyScreen));
    }

    private const int SystemMetricCxScreen = 0;
    private const int SystemMetricCyScreen = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
