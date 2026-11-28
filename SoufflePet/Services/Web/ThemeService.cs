// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Globalization;
using Avalonia.Threading;
using Microsoft.Win32;
using Serilog;
using SoufflePet.Models;
using System.Runtime.InteropServices;

namespace SoufflePet.Services.Web;

/// <summary>OKLCH 分量；与 web/src/theme/oklch.ts 同一套公式。</summary>
public readonly record struct OklchColor(double L, double C, double H);

/// <summary>sRGB ↔ OKLCH 的最小实现（仅用于主题 DTO；gamut/对比度由页面负责）。</summary>
public static class ThemeColor
{
    public const string DefaultAccent = "#E0873C";

    /// <summary>把 #RRGGBB / RRGGBB 规范成 #RRGGBB；非法返回 null。</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var value = input.Trim();
        if (value.StartsWith('#')) value = value[1..];
        if (value.Length != 6 || !int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            return null;
        return $"#{value.ToUpperInvariant()}";
    }

    public static OklchColor FromHex(string hex)
    {
        var value = Normalize(hex) ?? DefaultAccent;
        var number = int.Parse(value[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var r = Linearize(((number >> 16) & 255) / 255.0);
        var g = Linearize(((number >> 8) & 255) / 255.0);
        var b = Linearize((number & 255) / 255.0);

        var l = 0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b;
        var m = 0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b;
        var s = 0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b;

        var lRoot = Math.Cbrt(l);
        var mRoot = Math.Cbrt(m);
        var sRoot = Math.Cbrt(s);

        var okL = 0.2104542553 * lRoot + 0.7936177850 * mRoot - 0.0040720468 * sRoot;
        var okA = 1.9779984951 * lRoot - 2.4285922050 * mRoot + 0.4505937099 * sRoot;
        var okB = 0.0259040371 * lRoot + 0.7827717662 * mRoot - 0.8086757660 * sRoot;

        var hue = Math.Atan2(okB, okA) * 180 / Math.PI;
        if (hue < 0) hue += 360;

        return new OklchColor(
            Math.Clamp(okL, 0, 1),
            Math.Max(0, Math.Sqrt(okA * okA + okB * okB)),
            hue);
    }

    private static double Linearize(double channel) =>
        channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
}

/// <summary>React UI 的外观模式取值；与 web/src/bridge/contracts.ts 的 ThemeModePreference 对齐。</summary>
public static class ThemeModes
{
    public const string Dark = "dark";
    public const string Light = "light";
    public const string System = "system";
}

/// <summary>
/// React UI 的主题服务：唯一权威的强调色 / 外观模式来源（设置 / 跟随系统），
/// 变化时广播给 WebBridgeServer，由它推 host.theme 给页面。
/// </summary>
public sealed class ThemeService : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<ThemeService>();

    private readonly SouffleSettings _settings;
    private readonly DispatcherTimer _systemTimer;
    private string _lastSystemAccent = string.Empty;
    private string _lastSystemMode = ThemeModes.Dark;
    private bool _disposed;

    public ThemeService(SouffleSettings settings)
    {
        _settings = settings;
        _systemTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _systemTimer.Tick += (_, _) => PollSystemPreference();

        _settings.PropertyChanged += OnSettingsChanged;
        SyncSystemTimer();
    }

    /// <summary>主题变化（强调色 / 外观模式 / 系统配色）。事件在 UI 线程触发。</summary>
    public event EventHandler<ThemeDto>? Changed;

    public ThemeDto GetTheme()
    {
        var followSystem = _settings.UiAccentFollowSystem;
        var accent = followSystem
            ? ReadSystemAccent() ?? NormalizeSettingsAccent() ?? ThemeColor.DefaultAccent
            : NormalizeSettingsAccent() ?? ThemeColor.DefaultAccent;

        return Build(accent, followSystem);
    }

    /// <summary>设置自定义强调色（同时关闭跟随系统）。</summary>
    public ThemeDto SetAccent(string accent)
    {
        var normalized = ThemeColor.Normalize(accent)
                         ?? throw new ArgumentException("强调色必须是 #RRGGBB", nameof(accent));

        _settings.UiAccentColor = normalized;
        _settings.UiAccentFollowSystem = false;
        _settings.Save();
        var theme = Build(normalized, false);
        Raise(theme);
        return theme;
    }

    /// <summary>开启/关闭跟随系统强调色。</summary>
    public ThemeDto SetFollowSystem(bool enabled)
    {
        _settings.UiAccentFollowSystem = enabled;
        _settings.Save();
        if (enabled) _lastSystemAccent = ReadSystemAccent() ?? string.Empty;
        SyncSystemTimer();

        var theme = GetTheme();
        Raise(theme);
        return theme;
    }

    /// <summary>恢复默认琥珀色并关闭跟随系统。</summary>
    public ThemeDto Reset()
    {
        _settings.UiAccentColor = string.Empty;
        _settings.UiAccentFollowSystem = false;
        _settings.Save();
        SyncSystemTimer();
        var theme = Build(ThemeColor.DefaultAccent, false);
        Raise(theme);
        return theme;
    }

    /// <summary>设置外观模式（dark / light / system）。</summary>
    public ThemeDto SetMode(string mode)
    {
        var normalized = ParseMode(mode)
                         ?? throw new ArgumentException("外观模式必须是 dark / light / system", nameof(mode));

        _settings.UiThemeMode = normalized;
        _settings.Save();
        SyncSystemTimer();

        var theme = GetTheme();
        Raise(theme);
        return theme;
    }

    /// <summary>只预览，不落盘；外观面板拖动滑杆时使用。</summary>
    public ThemeDto Preview(string accent)
    {
        var normalized = ThemeColor.Normalize(accent)
                         ?? throw new ArgumentException("强调色必须是 #RRGGBB", nameof(accent));
        return Build(normalized, false);
    }

    private ThemeDto Build(string accent, bool followSystem)
    {
        var preference = ParseMode(_settings.UiThemeMode) ?? ThemeModes.Dark;
        var mode = preference == ThemeModes.System
            ? (ReadSystemLightTheme() ? ThemeModes.Light : ThemeModes.Dark)
            : preference;
        var oklch = ThemeColor.FromHex(accent);
        return new ThemeDto(mode, preference, accent, oklch.L, oklch.C, oklch.H, followSystem);
    }

    /// <summary>把任意输入夹成三个合法值；不认识时返回 null（调用方决定回退还是报错）。</summary>
    private static string? ParseMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        ThemeModes.Dark => ThemeModes.Dark,
        ThemeModes.Light => ThemeModes.Light,
        ThemeModes.System => ThemeModes.System,
        _ => null,
    };

    private string? NormalizeSettingsAccent() => ThemeColor.Normalize(_settings.UiAccentColor);

    private void Raise(ThemeDto theme) => Changed?.Invoke(this, theme);

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SouffleSettings.UiAccentFollowSystem) or nameof(SouffleSettings.UiThemeMode))
            SyncSystemTimer();
    }

    /// <summary>需要轮询系统配色：跟随强调色，或外观模式为 system。</summary>
    private bool ShouldPollSystem =>
        _settings.UiAccentFollowSystem || ParseMode(_settings.UiThemeMode) == ThemeModes.System;

    private void SyncSystemTimer()
    {
        if (_disposed) return;
        if (ShouldPollSystem)
        {
            _lastSystemAccent = ReadSystemAccent() ?? string.Empty;
            _lastSystemMode = ReadSystemLightTheme() ? ThemeModes.Light : ThemeModes.Dark;
            _systemTimer.Start();
        }
        else
        {
            _systemTimer.Stop();
        }
    }

    /// <summary>每 5 秒读一次 DWM 配色与系统应用主题；变化才广播，避免页面反复重绘。</summary>
    private void PollSystemPreference()
    {
        var accentChanged = false;
        if (_settings.UiAccentFollowSystem)
        {
            var accent = ReadSystemAccent();
            if (accent is not null && !string.Equals(accent, _lastSystemAccent, StringComparison.OrdinalIgnoreCase))
            {
                _lastSystemAccent = accent;
                accentChanged = true;
            }
        }

        var modeChanged = false;
        if (ParseMode(_settings.UiThemeMode) == ThemeModes.System)
        {
            var mode = ReadSystemLightTheme() ? ThemeModes.Light : ThemeModes.Dark;
            if (!string.Equals(mode, _lastSystemMode, StringComparison.Ordinal))
            {
                _lastSystemMode = mode;
                modeChanged = true;
            }
        }

        if (accentChanged || modeChanged) Raise(GetTheme());
    }

    /// <summary>Windows「应用主题」：AppsUseLightTheme = 1 表示浅色；读取失败按深色处理。</summary>
    private static bool ReadSystemLightTheme()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return false;
            var raw = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                null);
            return raw is int value && value != 0;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "read system theme mode failed");
            return false;
        }
    }

    /// <summary>DWM 的 ColorizationColor；失败时回退注册表 HKCU\...\DWM\ColorizationColor。</summary>
    private static string? ReadSystemAccent()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var hr = DwmGetColorizationColor(out var argb, out _);
                if (hr == 0)
                {
                    var hex = FromArgb(argb);
                    if (hex is not null) return hex;
                }

                var raw = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM",
                    "ColorizationColor",
                    null);
                if (raw is int value && FromArgb(unchecked((uint)value)) is { } registryHex)
                    return registryHex;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "read system accent failed");
        }

        return null;
    }

    private static string? FromArgb(uint argb)
    {
        var rgb = argb & 0x00FFFFFFu;
        // 纯黑/纯白通常是 DWM 未启用或读取失败，不拿它覆盖用户颜色
        if (rgb is 0 or 0xFFFFFFu) return null;
        return $"#{rgb:X6}";
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetColorizationColor(
        out uint pcrColorization,
        [MarshalAs(UnmanagedType.Bool)] out bool pfOpaqueBlend);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.PropertyChanged -= OnSettingsChanged;
        _systemTimer.Stop();
    }
}