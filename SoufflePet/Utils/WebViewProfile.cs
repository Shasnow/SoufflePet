// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Platform;

namespace SoufflePet.Utils;

/// <summary>
/// WebView2 用户数据目录（缓存、Cookie、证书、着色器缓存等数百个文件）的落盘位置。
/// 不指定时运行时默认写在可执行文件同级目录（<c>SoufflePet.exe.WebView2</c>），
/// 既污染安装目录，装到 Program Files 这类只读目录下还会直接启动失败；
/// 这里统一改到 <c>%LocalAppData%\SoufflePet\WebView2</c>。
/// 放 Local 而非 Roaming：这些都是可重建的缓存类数据，不该跟着漫游配置走。
/// </summary>
internal static class WebViewProfile
{
    /// <summary>用户数据目录：目录本身由 WebView2 运行时按需创建。</summary>
    public static string UserDataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SoufflePet",
        "WebView2");

    /// <summary>
    /// 把用户数据目录应用到环境请求上。只在 Windows WebView2 后端生效，
    /// 其它平台后端（WebKitGTK、WKWebView 等）各有自己的目录参数，这里不处理。
    /// </summary>
    public static void Apply(WebViewEnvironmentRequestedEventArgs e)
    {
        if (e is WindowsWebView2EnvironmentRequestedEventArgs webView2)
            webView2.UserDataFolder = UserDataFolder;
    }
}