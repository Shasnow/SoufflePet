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
using SoufflePet.Plugins.BrowserUse.Browser;

namespace SoufflePet.Plugins.BrowserUse;

/// <summary>
/// 浏览器操作插件：自带浏览器窗口（Avalonia + WebView2），向模型提供打开网页、读取页面、
/// 点击、输入、按键、滚动、后退、关闭与截图九个工具，全部由插件自身实现。
/// </summary>
public sealed class BrowserUsePlugin : PluginBase
{
    private readonly BrowserHost _browser = new();

    protected override void OnToolsBuilding(ICollection<AIFunction> tools)
    {
        tools.Add(AIFunctionFactory.Create(
            BrowserOpen,
            "browser_open",
            "在浏览器窗口里打开网址"));
        tools.Add(AIFunctionFactory.Create(
            BrowserSnapshot,
            "browser_snapshot",
            "读取浏览器当前网页的标题、可交互元素和正文"));
        tools.Add(AIFunctionFactory.Create(
            BrowserClick,
            "browser_click",
            "点击浏览器网页上的某个元素"));
        tools.Add(AIFunctionFactory.Create(
            BrowserType,
            "browser_type",
            "向浏览器网页的输入框写入文本，可选择先清空原有内容、写入后直接提交所在表单"));
        tools.Add(AIFunctionFactory.Create(
            BrowserPressKey,
            "browser_press_key",
            "向浏览器网页派发按键事件，作用于当前焦点元素或指定引用"));
        tools.Add(AIFunctionFactory.Create(
            BrowserScroll,
            "browser_scroll",
            "滚动浏览器网页或其中某个元素"));
        tools.Add(AIFunctionFactory.Create(
            BrowserBack,
            "browser_back",
            "浏览器返回上一页"));
        tools.Add(AIFunctionFactory.Create(
            BrowserClose,
            "browser_close",
            "关闭浏览器窗口并释放内存"));
        tools.Add(AIFunctionFactory.Create(
            BrowserScreenshot,
            "browser_screenshot",
            "截取浏览器窗口画面。"));

        Context.Logger.LogInformation("已就绪，共 {Count} 个工具", tools.Count);
    }

    /// <summary>禁用或退出时关闭浏览器窗口，避免插件卸载后残留 WebView2 进程。</summary>
    public override async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _browser.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Context.Logger.LogWarning(ex, "关闭浏览器窗口失败");
        }
    }

    /// <summary>打开浏览器并访问网址（懒创建窗口，只放行 http/https）。</summary>
    private Task<string> BrowserOpen(
        [Description("要打开的网址，例如 example.com、https://example.com/page")]
        string url,
        CancellationToken cancellationToken = default)
        => _browser.OpenAsync(url, cancellationToken);

    /// <summary>读取当前网页：标题、可交互元素（带引用编号）与正文。</summary>
    private Task<string> BrowserSnapshot(CancellationToken cancellationToken = default)
        => _browser.SnapshotAsync(cancellationToken);

    /// <summary>点击网页元素。</summary>
    private Task<string> BrowserClick(
        [Description("元素引用，如 e3")]
        string reference,
        CancellationToken cancellationToken = default)
        => _browser.ClickAsync(reference, cancellationToken);

    /// <summary>向网页输入框写入文本，可选择清空原值与直接提交。</summary>
    private Task<string> BrowserType(
        [Description("元素引用，如 e3")]
        string reference,
        [Description("要写入的文本")]
        string text,
        [Description("是否先清空原有内容，默认 true")]
        bool clear = true,
        [Description("是否在写入后提交所在表单，默认 false")]
        bool submit = false,
        CancellationToken cancellationToken = default)
        => _browser.TypeAsync(reference, text, clear, submit, cancellationToken);

    /// <summary>向网页派发按键事件。</summary>
    private Task<string> BrowserPressKey(
        [Description("按键名：Enter、Tab、Escape、Backspace、Delete、Home、End、PageUp、PageDown、方向键或单个字符")]
        string key,
        [Description("作用元素的引用，如 e3；不传则作用于当前焦点元素")]
        string? reference = null,
        CancellationToken cancellationToken = default)
        => _browser.PressKeyAsync(key, reference, cancellationToken);

    /// <summary>滚动网页或指定元素。</summary>
    private Task<string> BrowserScroll(
        [Description("滚动像素数，正数向下、负数向上")]
        int deltaY,
        [Description("要滚动的元素引用，如 e3；不传则滚动整个页面")]
        string? reference = null,
        CancellationToken cancellationToken = default)
        => _browser.ScrollAsync(deltaY, reference, cancellationToken);

    /// <summary>返回浏览器上一页。</summary>
    private Task<string> BrowserBack(CancellationToken cancellationToken = default)
        => _browser.BackAsync(cancellationToken);

    /// <summary>关闭浏览器窗口并释放其占用。</summary>
    private Task<string> BrowserClose(CancellationToken cancellationToken = default)
        => _browser.CloseAsync(cancellationToken);

    /// <summary>
    /// 截取浏览器窗口：与 take_screenshot 一样以 AIContent 集合返回，
    /// 对话服务据此把图片拆出来走多模态通道。
    /// </summary>
    private async Task<AIContent[]> BrowserScreenshot(CancellationToken cancellationToken = default)
    {
        var shot = await _browser.ScreenshotAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            new TextContent(
                $"已截取浏览器窗口：{shot.Width}×{shot.Height} 像素，文件 {shot.FilePath}。图片可能被缩小。"),
            new DataContent(shot.Image, shot.MediaType)
        ];
    }
}
