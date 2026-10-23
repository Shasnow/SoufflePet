// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SoufflePet.Plugins.InputKit.Win32;

/// <summary>
/// 真实键盘输入（Win32 SendInput）：单键按下松开、组合键、文本输入。
/// 事件发往当前获得键盘焦点的窗口，调用方需自行保证目标窗口在前台。
/// </summary>
[SupportedOSPlatform("windows")]
public static class KeyboardInput
{
    private const uint InputKeyboard = 1;

    /// <summary>按键松开。</summary>
    private const uint KeyUp = 0x0002;

    /// <summary>按 Unicode 字符发送（wScan 填字符本身，wVk 填 0）。</summary>
    private const uint Unicode = 0x0004;

    private const ushort VirtualKeyEnter = 0x0D;
    private const ushort VirtualKeyTab = 0x09;

    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = VirtualKeyEnter,
        ["return"] = VirtualKeyEnter,
        ["tab"] = VirtualKeyTab,
        ["space"] = 0x20,
        ["esc"] = 0x1B,
        ["escape"] = 0x1B,
        ["backspace"] = 0x08,
        ["delete"] = 0x2E,
        ["insert"] = 0x2D,
        ["home"] = 0x24,
        ["end"] = 0x23,
        ["pageup"] = 0x21,
        ["pagedown"] = 0x22,
        ["up"] = 0x26,
        ["down"] = 0x28,
        ["left"] = 0x25,
        ["right"] = 0x27,
        ["ctrl"] = 0x11,
        ["control"] = 0x11,
        ["shift"] = 0x10,
        ["alt"] = 0x12,
        ["win"] = 0x5B,
        ["meta"] = 0x5B,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        // MOUSEINPUT 本身用不到，但 SendInput 要求 cbSize 等于完整 INPUT 的大小（x64 上 40 字节），
        // 少了这个更大的联合成员结构体会被算小，调用直接失败。
        [FieldOffset(0)] public MouseInputData Mouse;

        [FieldOffset(0)] public KeyboardInputData Keyboard;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    /// <summary>按下并松开一个键；键名见 <see cref="ParseKey" /> 支持的写法。</summary>
    public static void Press(string key)
    {
        var virtualKey = ParseKey(key);
        Send([KeyEvent(virtualKey, down: true), KeyEvent(virtualKey, down: false)]);
    }

    /// <summary>按住修饰键 → 按下主键 → 松开主键 → 逆序松开修饰键，例如 ctrl+shift+s。</summary>
    public static void Hotkey(string keys)
    {
        var parts = keys.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) throw new ArgumentException("组合键不能为空。", nameof(keys));

        var main = ParseKey(parts[^1]);
        var modifiers = new List<ushort>(parts.Length - 1);
        for (var i = 0; i < parts.Length - 1; i++) modifiers.Add(ParseModifier(parts[i]));

        var events = new List<KeyboardInputData>(modifiers.Count * 2 + 2);
        foreach (var modifier in modifiers) events.Add(KeyEvent(modifier, down: true));
        events.Add(KeyEvent(main, down: true));
        events.Add(KeyEvent(main, down: false));
        for (var i = modifiers.Count - 1; i >= 0; i--) events.Add(KeyEvent(modifiers[i], down: false));

        Send(events);
    }

    /// <summary>逐字符输入文本（Unicode 通道，支持中文等非 ASCII 字符）；换行和制表符按对应按键发送。</summary>
    public static void TypeText(string text)
    {
        foreach (var ch in text.Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            switch (ch)
            {
                case '\n':
                    Press("enter");
                    break;
                case '\t':
                    Press("tab");
                    break;
                default:
                    Send([UnicodeEvent(ch, down: true), UnicodeEvent(ch, down: false)]);
                    break;
            }
        }
    }

    /// <summary>键名解析：字母、数字、f1-f24 与常用键名（enter、esc、tab、space、方向键等）。</summary>
    private static ushort ParseKey(string key)
    {
        var name = key.Trim();
        if (name.Length == 0) throw new ArgumentException("键名不能为空。", nameof(key));

        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]))
            return char.ToUpperInvariant(name[0]);

        if (NamedKeys.TryGetValue(name, out var named)) return named;

        if (name.Length is 2 or 3 && (name[0] is 'f' or 'F')
            && int.TryParse(name[1..], out var index) && index is >= 1 and <= 24)
            return (ushort)(0x70 + index - 1);

        throw new ArgumentException(
            $"不支持的键名「{key}」，可用字母、数字、f1-f24，以及 enter、esc、tab、space、"
            + "backspace、delete、insert、home、end、pageup、pagedown、up、down、left、right、ctrl、shift、alt、win。",
            nameof(key));
    }

    private static ushort ParseModifier(string name) => name.Trim().ToLowerInvariant() switch
    {
        "ctrl" or "control" => 0x11,
        "shift" => 0x10,
        "alt" => 0x12,
        "win" or "meta" or "cmd" => 0x5B,
        _ => throw new ArgumentException(
            $"不支持的修饰键「{name}」，可用 ctrl、shift、alt、win。", nameof(name))
    };

    private static KeyboardInputData KeyEvent(ushort virtualKey, bool down) => new()
    {
        VirtualKey = virtualKey,
        Flags = down ? 0 : KeyUp
    };

    private static KeyboardInputData UnicodeEvent(char ch, bool down) => new()
    {
        ScanCode = ch,
        Flags = Unicode | (down ? 0u : KeyUp)
    };

    private static void Send(IReadOnlyList<KeyboardInputData> events)
    {
        var inputs = new Input[events.Count];
        for (var i = 0; i < events.Count; i++)
            inputs[i] = new Input { Type = InputKeyboard, Data = new InputUnion { Keyboard = events[i] } };

        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new InvalidOperationException($"键盘事件发送失败（错误码 {Marshal.GetLastWin32Error()}）。");
    }
}
