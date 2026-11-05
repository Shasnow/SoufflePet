// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.Collections.Generic;
using SoufflePet.Utils;

namespace SoufflePet.Services;

/// <summary>
/// 把宿主静态 ScreenCapture 包装成 Core 可用的 IScreenCapture。
/// 宿主只在 Windows 上运行；调用方（ChatTools）已用 OperatingSystem.IsWindows 把关。
/// </summary>
public sealed class ScreenCaptureService : IScreenCapture
{
#pragma warning disable CA1416 // Windows-only：宿主进程只在 Windows 上运行
    public MonitorScreenshot CaptureCurrentMonitor() => ScreenCapture.CaptureCurrentMonitor();

    public MonitorScreenshot CaptureWindow(string title) => ScreenCapture.CaptureWindow(title);

    public IReadOnlyList<WindowTarget> GetWindows() => ScreenCapture.GetWindows();
#pragma warning restore CA1416
}