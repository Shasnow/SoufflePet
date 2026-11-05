// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace SoufflePet.Utils;

/// <summary>窗口停靠与几何夹紧：旧 ChatWindow 与新 AppWebWindow 共用。</summary>
public static class WindowPlacement
{
    /// <summary>与桌宠窗口之间的间距（DIP）。</summary>
    public const double GapDip = 12;

    /// <summary>
    /// 停靠定位：优先放在桌宠窗口左侧（右侧放不下时换到右侧），底边对齐；
    /// 尺寸从 DIP 换算成物理像素后按工作区夹紧。
    /// </summary>
    public static void PositionNear(Window window, Window anchor, double? widthDip = null, double? heightDip = null)
    {
        var screen = anchor.Screens.ScreenFromWindow(anchor) ?? anchor.Screens.Primary;
        if (screen is null) return;

        var wa = screen.WorkingArea;
        var scaling = anchor.RenderScaling > 0 ? anchor.RenderScaling : 1.0;
        var width = widthDip ?? (window.Bounds.Width > 0 ? window.Bounds.Width : window.Width);
        var height = heightDip ?? (window.Bounds.Height > 0 ? window.Bounds.Height : window.Height);
        var w = (int)Math.Round(width * scaling);
        var h = (int)Math.Round(height * scaling);
        var gap = (int)Math.Round(GapDip * scaling);
        var anchorW = (int)Math.Round(anchor.Bounds.Width * scaling);
        var anchorH = (int)Math.Round(anchor.Bounds.Height * scaling);

        var x = anchor.Position.X - w - gap;
        if (x < wa.X) x = anchor.Position.X + anchorW + gap;

        var maxX = Math.Max(wa.X, wa.Right - w);
        var maxY = Math.Max(wa.Y, wa.Bottom - h);
        window.Position = new PixelPoint(
            Math.Clamp(x, wa.X, maxX),
            Math.Clamp(anchor.Position.Y + anchorH - h, wa.Y, maxY));
    }

    /// <summary>
    /// 把保存的位置夹到包含它的显示器工作区；找不到时回退主屏。
    /// position 是物理像素，width/height 是 DIP。
    /// </summary>
    public static PixelPoint ClampToWorkingArea(Window window, PixelPoint position, double widthDip, double heightDip)
    {
        var screens = window.Screens;
        var screen = screens.All.FirstOrDefault(s => Contains(s.WorkingArea, position)) ?? screens.Primary;
        if (screen is null) return position;

        var wa = screen.WorkingArea;
        var scaling = window.RenderScaling > 0 ? window.RenderScaling : 1.0;
        var w = (int)Math.Round(Math.Max(1, widthDip) * scaling);
        var h = (int)Math.Round(Math.Max(1, heightDip) * scaling);
        var maxX = Math.Max(wa.X, wa.Right - w);
        var maxY = Math.Max(wa.Y, wa.Bottom - h);
        return new PixelPoint(
            Math.Clamp(position.X, wa.X, maxX),
            Math.Clamp(position.Y, wa.Y, maxY));
    }

    private static bool Contains(PixelRect rect, PixelPoint point) =>
        point.X >= rect.X && point.X < rect.Right && point.Y >= rect.Y && point.Y < rect.Bottom;
}