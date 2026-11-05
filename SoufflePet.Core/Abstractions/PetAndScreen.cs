// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SoufflePet.Services;

/// <summary>
/// 桌宠表情控制：ChatTools 只依赖这个接口，宿主用 PetExpressionService（绑定 IPetWindow）实现。
/// </summary>
public interface IPetController
{
    /// <summary>当前模型声明的表情名（未配置时为空）。</summary>
    IReadOnlyList<string> GetExpressionNames();

    /// <summary>当前窗口实际显示的表情名（null = 默认/窗口未就绪）。</summary>
    string? GetCurrentExpression();

    /// <summary>切换表情（空名字表示清除）；返回给模型看的说明。</summary>
    Task<string> SetExpressionAsync(string? name);
}

/// <summary>一次显示器截图的结果（图片、格式、落盘路径、原始尺寸与屏幕左上角）。</summary>
public sealed record MonitorScreenshot(
    byte[] Image,
    string MediaType,
    string FilePath,
    int Width,
    int Height,
    int ScreenLeft,
    int ScreenTop);

/// <summary>一个可截图的顶层窗口。</summary>
public sealed record WindowTarget(IntPtr Handle, string Title, string ProcessName, bool IsMinimized);

/// <summary>
/// 截图能力抽象：宿主用 GDI+/PrintWindow 实现；Core 的工具只拿结果，不引用 Win32 类型。
/// </summary>
public interface IScreenCapture
{
    MonitorScreenshot CaptureCurrentMonitor();
    MonitorScreenshot CaptureWindow(string title);
    IReadOnlyList<WindowTarget> GetWindows();
}

/// <summary>
/// UI 线程序列化抽象：Core 不引用 Avalonia.Dispatcher，ScheduledTaskService 通过它把
/// 集合/属性变更与触发回调放回 UI 线程。测试用同步实现。
/// </summary>
public interface IUiDispatcher
{
    bool IsUiThread { get; }
    void Post(Action action);
    void Invoke(Action action);
}