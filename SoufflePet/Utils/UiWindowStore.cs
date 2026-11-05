// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.IO;
using System.Text.Json;
using Serilog;
using SoufflePet.Services;

namespace SoufflePet.Utils;

/// <summary>主窗口几何（物理像素位置 + DIP 尺寸）；与历史窗口轮数的 window.json 分开。</summary>
public sealed record UiWindowState(int X, int Y, double Width, double Height, bool Maximized);

/// <summary>%AppData%\SoufflePet\ui-window.json 的读写；损坏/缺失时返回 null。</summary>
public static class UiWindowStore
{
    private static readonly ILogger Logger = Log.ForContext(typeof(UiWindowStore));

    private static readonly string Path = System.IO.Path.Combine(
        ChatMemory.DefaultRoot,
        "ui-window.json");

    public static UiWindowState? Load()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            var state = JsonSerializer.Deserialize<UiWindowState>(File.ReadAllText(Path));
            if (state is null || state.Width < 200 || state.Height < 200) return null;
            return state;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "load ui window state failed");
            return null;
        }
    }

    public static void Save(UiWindowState state)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(Path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "save ui window state failed");
        }
    }
}