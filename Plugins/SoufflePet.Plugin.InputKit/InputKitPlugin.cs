// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SoufflePet.Plugin.Sdk;
using SoufflePet.Plugins.InputKit.Win32;

namespace SoufflePet.Plugins.InputKit;

/// <summary>
/// 键鼠输入插件：向模型提供鼠标点击/拖拽/滚轮与键盘按键/组合键/文本输入六个工具。
/// 坐标一律是屏幕物理像素（左上角为原点），与 take_screenshot 返回的分辨率一致。
/// </summary>
public sealed class InputKitPlugin : PluginBase
{
    protected override void OnToolsBuilding(ICollection<AIFunction> tools)
    {
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
