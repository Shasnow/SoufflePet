// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.Threading;
using System.Threading.Tasks;

namespace SoufflePet.Services;

/// <summary>
/// 内置浏览器自动化：ChatTools 依赖此抽象；宿主 BrowserService（WebView2）实现。
/// </summary>
public interface IBrowserService
{
    Task<string> OpenAsync(string url, CancellationToken cancellationToken);
    Task<string> SnapshotAsync(CancellationToken cancellationToken);
    Task<string> ClickAsync(string reference, CancellationToken cancellationToken);
    Task<string> TypeAsync(string reference, string text, bool clear, bool submit, CancellationToken cancellationToken);
    Task<string> PressKeyAsync(string key, string? reference, CancellationToken cancellationToken);
    Task<string> ScrollAsync(int deltaY, string? reference, CancellationToken cancellationToken);
    Task<string> BackAsync(CancellationToken cancellationToken);
    Task<string> CloseAsync(CancellationToken cancellationToken);
    Task<MonitorScreenshot> ScreenshotAsync(CancellationToken cancellationToken);
}