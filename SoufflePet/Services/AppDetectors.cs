// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Serilog;
using SoufflePet.Models;

namespace SoufflePet.Services;

/// <summary>
/// 应用检测策略接口：两种独立方案（进程检测 / 前台窗口检测）各实现一份。
/// 每轮轮询只调用一次 Capture() 采集快照，之后各条目用 IsMatch() 查表判定，
/// 避免 N 个条目触发 N 次进程枚举，保证低资源消耗。
/// </summary>
public interface IAppDetector
{
    /// <summary>当前平台是否支持该检测方案（前台窗口检测在非 Windows 返回 false）。</summary>
    bool Supported { get; }

    /// <summary>采集一次快照（每轮轮询开始时调用）。</summary>
    void Capture();

    /// <summary>用本轮快照判定条目是否命中。</summary>
    bool IsMatch(AppEntry entry);
}

/// <summary>进程检测方案的标记接口（与前台检测区分注册，避免 IEnumerable&lt;IAppDetector&gt; 语义模糊）。</summary>
public interface IProcessDetector : IAppDetector;

/// <summary>前台窗口检测方案的标记接口。</summary>
public interface IForegroundDetector : IAppDetector;

/// <summary>
/// 方案一：进程检测。枚举系统进程名，判定目标进程是否在运行。
/// 适合"后台运行也算参与"的程序（游戏全屏无稳定标题、音乐播放器后台播放）。
/// 基于 System.Diagnostics.Process，天然跨平台；每轮只枚举一次。
/// </summary>
public sealed class ProcessDetector : IProcessDetector
{
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>进程检测天然跨平台，恒为 true。</summary>
    public bool Supported => true;

    public void Capture()
    {
        _running.Clear();
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    _running.Add(p.ProcessName);
                }
                catch
                {
                    // 进程在枚举瞬间退出时 ProcessName 会抛异常，忽略
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Log.ForContext<ProcessDetector>().Warning(ex, "enumerate processes failed");
        }
    }

    public bool IsMatch(AppEntry entry) => _running.Contains(entry.Process);
}

/// <summary>
/// 方案二：前台窗口检测。取当前激活窗口的标题进行匹配（不做进程反查）。
/// 适合"窗口在前台才有意义"的程序（视频播放器、浏览器等）。
/// 仅 Windows 支持（Win32 窗口管理机制）；非 Windows 平台 Supported=false，
/// 调用方应回退到进程检测，不抛异常。
/// </summary>
public sealed class ForegroundDetector : IForegroundDetector
{
    private static readonly ILogger Logger = Log.ForContext<ForegroundDetector>();

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    // 前台窗口标题快照（每轮 Capture 更新）
    private string _title = "";

    // 每个前台条目的正则缓存（null = 子串匹配；条目索引为键）
    private readonly Dictionary<int, Regex?> _regexCache = new();
    private readonly StringBuilder _sb = new(512);

    public bool Supported => OperatingSystem.IsWindows();

    public void Capture()
    {
        _title = "";
        if (!Supported) return;

        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;

        var len = GetWindowTextLength(hwnd);
        if (len > 0)
        {
            _sb.EnsureCapacity(len + 1);
            GetWindowText(hwnd, _sb, _sb.Capacity);
            _title = _sb.ToString();
        }
    }

    /// <summary>标题匹配：/…/ 形式为正则，否则忽略大小写子串（防抖在服务层做）。</summary>
    public bool IsMatch(AppEntry entry)
    {
        if (!Supported) return false;

        // 标题为空（无标题窗口）不判中
        if (_title.Length == 0) return false;

        var pattern = entry.TitlePattern;
        if (string.IsNullOrEmpty(pattern)) return false;
        if (pattern.Length > 1 && pattern[0] == '/' && pattern[^1] == '/')
        {
            var regex = GetRegex(entry, pattern[1..^1]);
            return regex?.IsMatch(_title) == true;
        }
        return _title.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>按条目索引缓存已编译正则；构造失败记英文警告并降级为子串匹配（返回 null）。</summary>
    private Regex? GetRegex(AppEntry entry, string body)
    {
        var idx = entry.Index;
        if (_regexCache.TryGetValue(idx, out var cached)) return cached;
        Regex? regex = null;
        try
        {
            regex = new Regex(body, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(500));
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "invalid titlePattern regex for {Process}, falling back to substring", entry.Process);
        }
        _regexCache[idx] = regex;
        return regex;
    }
}
