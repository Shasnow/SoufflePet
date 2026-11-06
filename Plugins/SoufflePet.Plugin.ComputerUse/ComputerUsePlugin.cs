// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SoufflePet.Plugin.Sdk;
using SoufflePet.Plugins.ComputerUse.Win32;

namespace SoufflePet.Plugins.ComputerUse;

/// <summary>
/// 电脑操作插件：向模型提供列出窗口、截屏，以及鼠标点击/拖拽/滚轮与键盘按键/组合键/文本输入工具。
/// 坐标一律是屏幕物理像素（左上角为原点），与 take_screenshot 返回的分辨率一致。
/// </summary>
public sealed class ComputerUsePlugin : PluginBase
{
    protected override void OnToolsBuilding(ICollection<AIFunction> tools)
    {
        tools.Add(AIFunctionFactory.Create(
            ListWindows,
            "list_windows",
            "列出当前屏幕上的可见窗口"));
        tools.Add(AIFunctionFactory.Create(
            TakeScreenshot,
            "take_screenshot",
            "截取屏幕或窗口画面"));
        tools.Add(AIFunctionFactory.Create(
            MouseClick,
            "mouse_click",
            "在屏幕坐标处点击鼠标。坐标是屏幕物理像素，原点为屏幕左上角；"));
        tools.Add(AIFunctionFactory.Create(
            MouseDrag,
            "mouse_drag",
            "按住鼠标从起点拖到终点后松开，用于拖动窗口、滑块、选择文本等。"));
        tools.Add(AIFunctionFactory.Create(
            MouseScroll,
            "mouse_scroll",
            "在屏幕坐标处滚动鼠标滚轮，正数向上、负数向下。"));
        tools.Add(AIFunctionFactory.Create(
            KeyboardPress,
            "keyboard_press",
            "按下并松开一个按键。"));
        tools.Add(AIFunctionFactory.Create(
            KeyboardHotkey,
            "keyboard_hotkey",
            "按下一组组合键，如 ctrl+s、alt+tab、win+d。"));
        tools.Add(AIFunctionFactory.Create(
            KeyboardType,
            "keyboard_type",
            "向当前焦点窗口输入文本，支持中文等 Unicode 字符。"));

        Context.Logger.LogInformation("已就绪，共 {Count} 个工具", tools.Count);
    }

    /// <summary>
    /// 截图结果直接以 AIContent 集合返回：对话服务会把图片拆出来走多模态通道。
    /// </summary>
    private static AIContent[] TakeScreenshot(
        [Description("窗口标题，可只写一部分；不传则截取光标所在显示器")]
        string? window = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("截图工具仅支持 Windows。");

        var target = window?.Trim();
        var shot = string.IsNullOrEmpty(target)
            ? ScreenCapture.CaptureCurrentMonitor()
            : ScreenCapture.CaptureWindow(target);
        var label = string.IsNullOrEmpty(target) ? "当前显示器" : $"窗口「{target}」";
        return
        [
            new TextContent(
                $"已截取{label}：{shot.Width}×{shot.Height} 像素，左上角屏幕坐标 ({shot.ScreenLeft}, {shot.ScreenTop})，"
                + $"文件 {shot.FilePath}。图片可能被缩小，鼠标坐标需按比例换算后加偏移。"),
            new DataContent(shot.Image, shot.MediaType)
        ];
    }

    /// <summary>列出可截图的窗口，让模型知道 window 参数该填什么。</summary>
    private static string ListWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("窗口列表仅支持 Windows。");

        var windows = ScreenCapture.GetWindows();
        if (windows.Count == 0) return "当前没有可见的窗口。";

        var builder = new StringBuilder("当前可见窗口：\n");
        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            builder.Append(i + 1).Append(". ").Append(window.Title)
                .Append('（').Append(string.IsNullOrEmpty(window.ProcessName) ? "未知进程" : window.ProcessName).Append('）');
            if (window.IsMinimized) builder.Append("（已最小化）");
            builder.Append('\n');
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>在屏幕坐标处单击/双击：Win32 鼠标事件会真的作用到光标下的程序上。</summary>
    private static string MouseClick(
        [Description("屏幕 X 坐标（物理像素）")]
        int x,
        [Description("屏幕 Y 坐标（物理像素）")]
        int y,
        [Description("按键：left、right、middle，默认 left")]
        string button = "left",
        [Description("是否双击，默认 false")]
        bool doubleClick = false)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("鼠标工具仅支持 Windows。");

        var mouseButton = ParseMouseButton(button);
        MouseInput.Click(x, y, mouseButton, doubleClick);
        return $"已在 ({x}, {y}) {(doubleClick ? "双击" : "单击")}{DescribeMouseButton(mouseButton)}。";
    }

    /// <summary>按住鼠标从起点拖到终点后松开：中途分步移动，适合拖窗口、滑块和选区。</summary>
    private static async Task<string> MouseDrag(
        [Description("起点 X 坐标（物理像素）")]
        int fromX,
        [Description("起点 Y 坐标（物理像素）")]
        int fromY,
        [Description("终点 X 坐标（物理像素）")]
        int toX,
        [Description("终点 Y 坐标（物理像素）")]
        int toY,
        [Description("按键：left、right、middle，默认 left")]
        string button = "left",
        [Description("拖拽时长（毫秒），默认 500，范围 100-5000")]
        int durationMs = 500,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("鼠标工具仅支持 Windows。");

        var mouseButton = ParseMouseButton(button);
        var duration = Math.Clamp(durationMs, 100, 5000);
        await MouseInput.DragAsync(fromX, fromY, toX, toY, mouseButton, duration, cancellationToken)
            .ConfigureAwait(false);
        return $"已用{DescribeMouseButton(mouseButton)}从 ({fromX}, {fromY}) 拖拽到 ({toX}, {toY})，用时约 {duration} 毫秒。";
    }

    /// <summary>在指定位置滚动滚轮：先移动光标，再按格数发送滚轮事件。</summary>
    private static string MouseScroll(
        [Description("屏幕 X 坐标（物理像素）")]
        int x,
        [Description("屏幕 Y 坐标（物理像素）")]
        int y,
        [Description("滚动格数：正数向上、负数向下，范围 -50 到 50")]
        int notches)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("鼠标工具仅支持 Windows。");

        var value = Math.Clamp(notches, -50, 50);
        MouseInput.Scroll(x, y, value);
        return $"已在 ({x}, {y}) 滚动 {value} 格（{(value >= 0 ? "向上" : "向下")}）。";
    }

    /// <summary>按下并松开一个键：Enter、Esc、方向键、F 键或单个字母数字。</summary>
    private static string KeyboardPress(
        [Description("键名：字母、数字、f1-f24，或 enter、esc、tab、space、backspace、delete、insert、"
                     + "home、end、pageup、pagedown、up、down、left、right、ctrl、shift、alt、win")]
        string key)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("键盘工具仅支持 Windows。");

        KeyboardInput.Press(key);
        return $"已按下 {key.Trim()}。";
    }

    /// <summary>按下组合键：修饰键按住，主键按下再松开，最后逆序松开修饰键。</summary>
    private static string KeyboardHotkey(
        [Description("组合键，用 + 连接，如 ctrl+shift+s、alt+tab、win+d")]
        string keys)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("键盘工具仅支持 Windows。");

        KeyboardInput.Hotkey(keys);
        return $"已按下组合键 {keys.Trim()}。";
    }

    /// <summary>逐字符输入文本：走 Unicode 通道，不受输入法影响。</summary>
    private static string KeyboardType(
        [Description("要输入的文本，可含换行与中文")]
        string text)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("键盘工具仅支持 Windows。");

        KeyboardInput.TypeText(text);
        return $"已输入 {text.Length} 个字符。";
    }

    private static MouseButton ParseMouseButton(string? button) => button?.Trim().ToLowerInvariant() switch
    {
        null or "" or "left" => MouseButton.Left,
        "right" => MouseButton.Right,
        "middle" => MouseButton.Middle,
        _ => throw new ArgumentException($"不支持的鼠标按键「{button}」，可用 left、right、middle。", nameof(button))
    };

    private static string DescribeMouseButton(MouseButton button) => button switch
    {
        MouseButton.Right => "右键",
        MouseButton.Middle => "中键",
        _ => "左键"
    };
}
