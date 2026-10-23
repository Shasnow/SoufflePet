// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace SoufflePet.Plugins.InputKit.Win32;

/// <summary>鼠标按键。</summary>
public enum MouseButton
{
    Left,
    Right,
    Middle
}

/// <summary>
/// 真实鼠标操作（Win32 SetCursorPos + SendInput）：单击/双击、按住拖拽、滚轮。
/// 坐标一律是屏幕物理像素（左上角为原点），与 take_screenshot 返回的分辨率一致。
/// </summary>
[SupportedOSPlatform("windows")]
public static class MouseInput
{
    private const uint InputMouse = 0;

    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint RightDown = 0x0008;
    private const uint RightUp = 0x0010;
    private const uint MiddleDown = 0x0020;
    private const uint MiddleUp = 0x0040;
    private const uint Wheel = 0x0800;

    /// <summary>滚轮一格的标准增量：MouseData 按它为单位传递（正数向上、负数向下）。</summary>
    private const int WheelDelta = 120;

    /// <summary>双击两次点击之间的间隔：小于系统双击间隔（默认 500ms）才会被识别成双击。</summary>
    private const int DoubleClickIntervalMs = 120;

    /// <summary>按下/松开之间的停顿，给目标程序留出处理消息的时间。</summary>
    private const int PressDelayMs = 30;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInputData Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    /// <summary>移动光标并在指定位置点击（可选双击）。</summary>
    public static void Click(int x, int y, MouseButton button = MouseButton.Left, bool doubleClick = false)
    {
        MoveTo(x, y);
        Press(button, down: true);
        Sleep(PressDelayMs);
        Press(button, down: false);

        if (!doubleClick) return;

        Sleep(DoubleClickIntervalMs);
        Press(button, down: true);
        Sleep(PressDelayMs);
        Press(button, down: false);
    }

    /// <summary>
    /// 按住鼠标从起点拖到终点后松开：中途分步移动，避免部分程序（画布、滑块）把瞬移当成点击。
    /// 取消或异常时也会松开按键，不会把用户的鼠标卡在按下状态。
    /// </summary>
    public static async Task DragAsync(
        int fromX,
        int fromY,
        int toX,
        int toY,
        MouseButton button = MouseButton.Left,
        int durationMs = 500,
        CancellationToken cancellationToken = default)
    {
        MoveTo(fromX, fromY);
        await Task.Delay(PressDelayMs, cancellationToken).ConfigureAwait(false);

        Press(button, down: true);
        try
        {
            var steps = Math.Clamp(durationMs / 10, 10, 100);
            var delay = Math.Max(1, durationMs / steps);
            for (var i = 1; i <= steps; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MoveTo(
                    fromX + (int)Math.Round((toX - fromX) * (double)i / steps),
                    fromY + (int)Math.Round((toY - fromY) * (double)i / steps));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Press(button, down: false);
        }
    }

    /// <summary>移动光标到指定位置并滚动滚轮：格数为正向上、为负向下。</summary>
    public static void Scroll(int x, int y, int notches)
    {
        MoveTo(x, y);

        var inputs = new[]
        {
            new Input
            {
                Type = InputMouse,
                Data = new InputUnion
                {
                    Mouse = new MouseInputData
                    {
                        // 负数按补码传给 MouseData（uint），系统据此识别为向下滚
                        MouseData = unchecked((uint)(notches * WheelDelta)),
                        Flags = Wheel
                    }
                }
            }
        };

        if (SendInput(1, inputs, Marshal.SizeOf<Input>()) != 1)
            throw new InvalidOperationException($"鼠标滚轮事件发送失败（错误码 {Marshal.GetLastWin32Error()}）。");
    }

    private static void MoveTo(int x, int y)
    {
        if (!SetCursorPos(x, y))
            throw new InvalidOperationException($"无法把鼠标移动到 ({x}, {y})。");
    }

    private static void Sleep(int milliseconds) => Thread.Sleep(milliseconds);

    private static void Press(MouseButton button, bool down)
    {
        var flags = (button, down) switch
        {
            (MouseButton.Left, true) => LeftDown,
            (MouseButton.Left, false) => LeftUp,
            (MouseButton.Right, true) => RightDown,
            (MouseButton.Right, false) => RightUp,
            (MouseButton.Middle, true) => MiddleDown,
            _ => MiddleUp
        };

        var inputs = new[]
        {
            new Input { Type = InputMouse, Data = new InputUnion { Mouse = new MouseInputData { Flags = flags } } }
        };

        if (SendInput(1, inputs, Marshal.SizeOf<Input>()) != 1)
            throw new InvalidOperationException($"鼠标按键事件发送失败（错误码 {Marshal.GetLastWin32Error()}）。");
    }
}
