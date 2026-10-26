// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Serilog;
using SoufflePet.Utils;
using SoufflePet.Views;

namespace SoufflePet.Services;

/// <summary>
/// 内置浏览器：由对话模型驱动 <see cref="BrowserWindow" /> 里的网页视图完成导航、读取与交互。
/// 所有操作经 UI 线程串行执行（模型可能并发发起多个调用），页面数据的读写统一走 InvokeScript，
/// 不需要额外的驱动库或 CDP 依赖。
/// </summary>
public interface IBrowserService
{
    /// <summary>打开网址（只允许 http/https），返回页面标题与加载状态。</summary>
    Task<string> OpenAsync(string url, CancellationToken cancellationToken);

    /// <summary>取出当前页面的标题、可交互元素（带引用编号）与正文，供模型决定下一步操作。</summary>
    Task<string> SnapshotAsync(CancellationToken cancellationToken);

    /// <summary>点击快照里某个引用编号对应的元素。</summary>
    Task<string> ClickAsync(string reference, CancellationToken cancellationToken);

    /// <summary>向快照里某个引用编号对应的输入框写入文本，可清空原值、可直接提交。</summary>
    Task<string> TypeAsync(
        string reference, string text, bool clear, bool submit, CancellationToken cancellationToken);

    /// <summary>向页面（或指定元素）派发一次按键事件。</summary>
    Task<string> PressKeyAsync(string key, string? reference, CancellationToken cancellationToken);

    /// <summary>滚动页面或指定元素。</summary>
    Task<string> ScrollAsync(int deltaY, string? reference, CancellationToken cancellationToken);

    /// <summary>返回上一页。</summary>
    Task<string> BackAsync(CancellationToken cancellationToken);

    /// <summary>关闭内置浏览器窗口并释放其占用（WebView2 视图随控件销毁）。</summary>
    Task<string> CloseAsync(CancellationToken cancellationToken);

    /// <summary>截取浏览器窗口画面（隐藏或最小化时先还原成可见的正常状态），图片回传给模型走多模态通道。</summary>
    Task<MonitorScreenshot> ScreenshotAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 内置浏览器服务（DI 单例）：懒创建窗口、串行化操作、把页面脚本的结果整理成模型可读的文本。
/// 窗口关闭只隐藏，登录态与浏览历史保留在会话里；应用退出时统一销毁。
/// </summary>
public sealed class BrowserService : IBrowserService, IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<BrowserService>();

    /// <summary>单次页面脚本的执行上限（秒）：脚本卡住时不能把对话一起拖死。</summary>
    private const int ScriptTimeoutSeconds = 20;

    /// <summary>一次导航的等待上限（秒）：超时通常意味着 WebView2 运行时缺失或网络不可达。</summary>
    private const int NavigationTimeoutSeconds = 60;

    /// <summary>回传给模型的可交互元素上限，超出的忽略（上下文有限，且模型也点不过来）。</summary>
    private const int MaxElements = 200;

    /// <summary>回传给模型的正文上限（字符），超出截断。</summary>
    private const int MaxPageText = 6000;

    /// <summary>点击/截图前的稳定等待：跳转与渲染都是异步的，立刻回读拿到的还是旧画面。</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>引用编号格式：快照生成 e0/e1/…，只有这种值才允许拼进脚本。</summary>
    private static readonly Regex ReferencePattern = new("^e[0-9]{1,4}$", RegexOptions.Compiled);

    /// <summary>允许派发的按键名：白名单避免把任意字符串拼进脚本。</summary>
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "Enter", "Tab", "Escape", "Backspace", "Delete", "Home", "End", "PageUp", "PageDown",
        "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", " ",
    };

    /// <summary>按键名到 keyCode 的映射：页面里的老代码常读 keyCode，不填会被当成 0 忽略。</summary>
    private static readonly Dictionary<string, int> KeyCodes = new(StringComparer.Ordinal)
    {
        ["Enter"] = 13, ["Tab"] = 9, ["Escape"] = 27, ["Backspace"] = 8, ["Delete"] = 46,
        ["Home"] = 36, ["End"] = 35, ["PageUp"] = 33, ["PageDown"] = 34, [" "] = 32,
        ["ArrowLeft"] = 37, ["ArrowUp"] = 38, ["ArrowRight"] = 39, ["ArrowDown"] = 40,
    };

    /// <summary>串行化闸门：模型并发调用多个浏览器工具时，保证同一时刻只有一个操作在动页面。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private BrowserWindow? _window;

    /// <summary>当前导航的完成信号：由窗口的 NavigationCompleted 事件置位。</summary>
    private TaskCompletionSource<bool>? _navigation;

    private bool _disposed;

    public Task<string> OpenAsync(string url, CancellationToken cancellationToken)
    {
        var uri = NormalizeUrl(url);
        return RunExclusiveAsync(async () =>
        {
            var window = await EnsureWindowAsync(cancellationToken).ConfigureAwait(false);
            var wait = BeginNavigation(cancellationToken);
            await Dispatcher.UIThread.InvokeAsync(() => window.View.Navigate(uri));
            if (!await wait.ConfigureAwait(false))
                throw new InvalidOperationException(
                    $"打开「{uri}」超时（{NavigationTimeoutSeconds} 秒）。可能是网络不可达，或系统缺少 WebView2 运行时。");

            var state = await RunJsonAsync(PageStateScript, cancellationToken).ConfigureAwait(false);
            var title = ReadString(state, "title");
            return $"已打开 {uri}\n标题：{(string.IsNullOrEmpty(title) ? "（空）" : title)}"
                + $"\n加载状态：{ReadString(state, "ready")}";
        }, cancellationToken);
    }

    public Task<string> SnapshotAsync(CancellationToken cancellationToken) =>
        RunExclusiveAsync(async () =>
        {
            await EnsureWindowAsync(cancellationToken).ConfigureAwait(false);
            var root = await RunJsonAsync(SnapshotScript, cancellationToken).ConfigureAwait(false);
            return FormatSnapshot(root);
        }, cancellationToken);

    public Task<string> ClickAsync(string reference, CancellationToken cancellationToken) =>
        RunExclusiveAsync(async () =>
        {
            var element = ValidateReference(reference);
            await EnsureWindowAsync(cancellationToken).ConfigureAwait(false);
            var script = $@"
(function () {{
  var el = document.querySelector('[data-sp-ref=""{element}""]');
  if (!el) return {{ error: '引用 {element} 已失效，请重新调用 browser_snapshot 获取最新引用。' }};
  el.scrollIntoView({{ block: 'center', inline: 'center' }});
  try {{ el.focus({{ preventScroll: true }}); }} catch (e) {{ }}
  var rect = el.getBoundingClientRect();
  var init = {{
    bubbles: true, cancelable: true, view: window,
    clientX: rect.left + rect.width / 2, clientY: rect.top + rect.height / 2
  }};
  try {{
    el.dispatchEvent(new MouseEvent('mousedown', init));
    el.dispatchEvent(new MouseEvent('mouseup', init));
    el.click();
  }} catch (e) {{ return {{ error: '点击失败：' + e.message }}; }}
  return {{ tag: el.tagName.toLowerCase(), text: (el.innerText || '').slice(0, 60) }};
}})()";
            var root = await RunJsonAsync(script, cancellationToken).ConfigureAwait(false);
            ThrowIfError(root);

            // 点击常会触发跳转或异步渲染：等一小段再回报当前地址，让模型据此决定是否重新快照
            await Task.Delay(SettleDelay, cancellationToken).ConfigureAwait(false);
            var state = await RunJsonAsync(PageStateScript, cancellationToken).ConfigureAwait(false);
            return $"已点击 {element}（{ReadString(root, "tag")}「{ReadString(root, "text")}」）"
                + $"\n当前地址：{ReadString(state, "url")}\n加载状态：{ReadString(state, "ready")}";
        }, cancellationToken);

    public Task<string> TypeAsync(
        string reference, string text, bool clear, bool submit, CancellationToken cancellationToken) =>
        RunExclusiveAsync(async () =>
        {
            var element = ValidateReference(reference);
            await EnsureWindowAsync(cancellationToken).ConfigureAwait(false);

            // 文本按 JSON 字面量注入：引号、换行、反斜杠都不会破坏脚本
            var literal = JsonSerializer.Serialize(text);
            var next = clear ? literal : $"(el.value || '') + {literal}";
            var script = $@"
(function () {{
  var el = document.querySelector('[data-sp-ref=""{element}""]');
  if (!el) return {{ error: '引用 {element} 已失效，请重新调用 browser_snapshot 获取最新引用。' }};
  el.scrollIntoView({{ block: 'center' }});
  try {{ el.focus({{ preventScroll: true }}); }} catch (e) {{ }}
  var next = {next};
  var proto = el.tagName === 'TEXTAREA' ? window.HTMLTextAreaElement.prototype
    : (el.tagName === 'SELECT' ? window.HTMLSelectElement.prototype : window.HTMLInputElement.prototype);
  var descriptor = Object.getOwnPropertyDescriptor(proto, 'value');
  if (descriptor && descriptor.set) descriptor.set.call(el, next); else el.value = next;
  el.dispatchEvent(new Event('input', {{ bubbles: true }}));
  el.dispatchEvent(new Event('change', {{ bubbles: true }}));
  var submitted = false;
  if ({submit.ToString().ToLowerInvariant()}) {{
    var form = el.form || (el.closest ? el.closest('form') : null);
    if (form && form.requestSubmit) {{ form.requestSubmit(el); submitted = true; }}
    else {{
      el.dispatchEvent(new KeyboardEvent('keydown', {{ key: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true }}));
      el.dispatchEvent(new KeyboardEvent('keyup', {{ key: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true }}));
    }}
  }}
  return {{ tag: el.tagName.toLowerCase(), value: String(el.value || '').slice(0, 60), submitted: submitted }};
}})()";
            var root = await RunJsonAsync(script, cancellationToken).ConfigureAwait(false);
            ThrowIfError(root);

            var submitted = root.TryGetProperty("submitted", out var flag) && flag.ValueKind == JsonValueKind.True;
            var tail = submitted
                ? "已提交所在表单"
                : "未提交（需要提交时把 submit 设为 true，或点击提交按钮）";
            return $"已写入 {element}，当前值「{ReadString(root, "value")}」，{tail}。";
        }, cancellationToken);

    public Task<string> PressKeyAsync(string key, string? reference, CancellationToken cancellationToken) =>
        RunExclusiveAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(key) || (key.Length > 1 && !AllowedKeys.Contains(key)))
                throw new ArgumentException($"按键名不合法：{key}。可用值：" + string.Join("、", AllowedKeys));
            var element = string.IsNullOrWhiteSpace(reference) ? null : ValidateReference(reference);
            await EnsureWindowAsync(cancellationToken).ConfigureAwait(false);

            var literal = JsonSerializer.Serialize(key);
            var code = KeyCodes.TryGetValue(key, out var known) ? known : 0;
            var target = element is null
                ? "document.activeElement || document.body"
                : $@"document.querySelector('[data-sp-ref=""{element}""]')";
            var script = $@"
(function () {{
  var el = {target};
  if (!el) return {{ error: '没有可接收按键的元素，请先点击目标输入框，或传入有效引用。' }};
  var init = {{ key: {literal}, code: {literal}, keyCode: {code}, which: {code}, bubbles: true, cancelable: true }};
  el.dispatchEvent(new KeyboardEvent('keydown', init));
  el.dispatchEvent(new KeyboardEvent('keypress', init));
  el.dispatchEvent(new KeyboardEvent('keyup', init));
  return {{ tag: el.tagName.toLowerCase() }};
}})()";
            var root = await RunJsonAsync(script, cancellationToken).ConfigureAwait(false);
            ThrowIfError(root);

            // 合成按键不会触发浏览器默认行为（回车提交、Tab 移动焦点），这一点要在结果里讲清楚
            return $"已派发按键 {key}（作用于 {ReadString(root, "tag")}）。"
                + "注意：合成按键只触发页面自己的监听，浏览器默认行为（回车提交、Tab 切换焦点）不会发生；"
                + "提交表单请用 browser_type 的 submit 参数或点击提交按钮。";
        }, cancellationToken);

    public Task<string> ScrollAsync(int deltaY, string? reference, CancellationToken cancellationToken) =>
        RunExclusiveAsync(async () =>
        {
            var element = string.IsNullOrWhiteSpace(reference) ? null : ValidateReference(reference);
            await EnsureWindowAsync(cancellationToken).ConfigureAwait(false);

            var script = element is null
                ? $@"
(function () {{
  window.scrollBy(0, {deltaY});
  return {{ url: location.href, y: window.scrollY, height: document.body ? document.body.scrollHeight : 0 }};
}})()"
                : $@"
(function () {{
  var el = document.querySelector('[data-sp-ref=""{element}""]');
  if (!el) return {{ error: '引用 {element} 已失效，请重新调用 browser_snapshot 获取最新引用。' }};
  el.scrollTop += {deltaY};
  return {{ url: location.href, y: el.scrollTop, height: el.scrollHeight }};
}})()";
            var root = await RunJsonAsync(script, cancellationToken).ConfigureAwait(false);
            ThrowIfError(root);

            var scope = element is null ? "页面" : $"元素 {element}";
            return $"已滚动{scope} {deltaY} 像素，当前偏移 {ReadNumber(root, "y")}/总高 {ReadNumber(root, "height")}。";
        }, cancellationToken);

    public Task<string> BackAsync(CancellationToken cancellationToken) =>
        RunExclusiveAsync(async () =>
        {
            var window = await EnsureWindowAsync(cancellationToken).ConfigureAwait(false);
            var wait = BeginNavigation(cancellationToken);
            var moved = await Dispatcher.UIThread.InvokeAsync(() => window.View.GoBack());
            if (!moved)
            {
                // 没有上一页：撤掉刚登记的等待信号，避免它一直挂着
                Interlocked.Exchange(ref _navigation, null)?.TrySetResult(false);
                return "已经在浏览历史的第一页，没有可返回的上一页。";
            }

            await wait.ConfigureAwait(false);
            var state = await RunJsonAsync(PageStateScript, cancellationToken).ConfigureAwait(false);
            var title = ReadString(state, "title");
            return $"已返回上一页：{ReadString(state, "url")}\n标题：{(string.IsNullOrEmpty(title) ? "（空）" : title)}";
        }, cancellationToken);

    /// <summary>
    /// 真正关闭浏览器窗口：摘掉网页控件并关闭窗口，WebView2 适配器随之销毁、
    /// 进程与内存交回系统；下次 browser_open 会重新创建一个干净的窗口。
    /// </summary>
    public async Task<string> CloseAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            // 窗口没了，导航等待信号跟着作废，别让它悬着等超时
            Interlocked.Exchange(ref _navigation, null)?.TrySetResult(false);

            var window = _window;
            if (window is null) return "内置浏览器还没有打开过，不需要关闭。";

            _window = null;
            await Dispatcher.UIThread.InvokeAsync(() => DestroyWindow(window));
            Logger.Information("browser window destroyed");
            return "已关闭内置浏览器并释放其占用。下次 browser_open 会重新创建窗口，当前页面与浏览历史不保留。";
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 销毁窗口（必须在 UI 线程调用）：先把网页控件从窗口内容上摘下来，
    /// 控件脱离可视树即销毁适配器，WebView2 进程不必等窗口关闭流程才退出。
    /// </summary>
    private void DestroyWindow(BrowserWindow window)
    {
        window.View.NavigationCompleted -= OnNavigationCompleted;
        window.Content = null;
        window.CloseForShutdown();
    }

    public async Task<MonitorScreenshot> ScreenshotAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("浏览器截图仅支持 Windows。");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var window = await EnsureWindowAsync(cancellationToken).ConfigureAwait(false);
            // 截图用 PrintWindow 抓窗口内容：隐藏或最小化的窗口都抓不到画面，先让它以正常状态显示出来
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!window.IsVisible) window.Show();
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            });
            await Task.Delay(SettleDelay, cancellationToken).ConfigureAwait(false);

            // 按固定标题定位本窗口（窗口标题不随页面变化，否则会找不到）
            return ScreenCapture.CaptureWindow(BrowserWindow.WindowTitle);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 串行执行一段浏览器操作：闸门保证同一时刻只有一个操作在动页面，
    /// 避免模型并发调用时互相打断（例如点击与快照竞争同一份引用编号）。
    /// </summary>
    private async Task<string> RunExclusiveAsync(
        Func<Task<string>> operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>懒创建浏览器窗口并复用；创建与显示都在 UI 线程完成。</summary>
    private async Task<BrowserWindow> EnsureWindowAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (_window is { } existing)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!existing.IsVisible) existing.Show();
            });
            return existing;
        }

        BrowserWindow? created = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            created = new BrowserWindow();
            created.View.NavigationCompleted += OnNavigationCompleted;
            created.Show();
        });

        _window = created ?? throw new InvalidOperationException("浏览器窗口创建失败。");
        Logger.Information("browser window created");
        return _window;
    }

    /// <summary>导航完成回调（UI 线程）：只置位等待信号，不做别的。</summary>
    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e) =>
        Interlocked.Exchange(ref _navigation, null)?.TrySetResult(e.IsSuccess);

    /// <summary>
    /// 先登记等待信号再发起导航，避免页面太快加载完、信号在登记前就错过。
    /// 返回 false 表示超时，或又被下一次导航顶掉。
    /// </summary>
    private Task<bool> BeginNavigation(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _navigation, tcs)?.TrySetResult(false);
        return WaitAsync(tcs.Task, cancellationToken);

        static async Task<bool> WaitAsync(Task<bool> navigation, CancellationToken token)
        {
            var timeout = Task.Delay(TimeSpan.FromSeconds(NavigationTimeoutSeconds), token);
            var completed = await Task.WhenAny(navigation, timeout).ConfigureAwait(false);
            return completed == navigation && await navigation.ConfigureAwait(false);
        }
    }

    /// <summary>执行一段页面脚本并解析出 JSON 对象；脚本自身抛错时包装成 error 字段回传。</summary>
    private async Task<JsonElement> RunJsonAsync(string expression, CancellationToken cancellationToken)
    {
        var window = _window ?? throw new InvalidOperationException("浏览器窗口尚未创建。");
        var script = "JSON.stringify((function () { try { return ("
            + expression
            + "); } catch (e) { return { error: String((e && e.message) || e) }; } })())";

        var raw = await InvokeScriptAsync(window.View, script)
            .WaitAsync(TimeSpan.FromSeconds(ScriptTimeoutSeconds), cancellationToken)
            .ConfigureAwait(false);

        var payload = UnwrapScriptResult(raw);
        if (string.IsNullOrEmpty(payload))
            throw new InvalidOperationException(
                "页面没有返回结果：网页可能仍在加载，或系统缺少 WebView2 运行时。");

        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// 在 UI 线程执行页面脚本并把返回值投递回来：用 Post + TaskCompletionSource 显式衔接，
    /// 比直接 await 调度器的返回值更直观（脚本自身是异步的，结果要等页面应答）。
    /// </summary>
    private static Task<string?> InvokeScriptAsync(NativeWebView view, string script)
    {
        var source = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() => _ = CompleteScriptAsync(view, script, source));
        return source.Task;
    }

    private static async Task CompleteScriptAsync(
        NativeWebView view, string script, TaskCompletionSource<string?> source)
    {
        try
        {
            source.TrySetResult(await view.InvokeScript(script));
        }
        catch (Exception ex)
        {
            source.TrySetException(ex);
        }
    }

    /// <summary>
    /// 适配器把脚本的返回值又序列化了一层（字符串 → 带引号的 JSON 字面量），这里还原成原始 JSON 文本。
    /// </summary>
    private static string? UnwrapScriptResult(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw[0] != '"') return raw;

        try
        {
            return JsonSerializer.Deserialize<string>(raw);
        }
        catch (JsonException)
        {
            // 不是合法的 JSON 字面量就按原样使用，宁可让解析在上层报错，也不在这里丢数据
            return raw;
        }
    }

    /// <summary>脚本内部捕获到的异常统一放进 error 字段，这里转成工具错误抛出去。</summary>
    private static void ThrowIfError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            throw new InvalidOperationException(error.GetString());
    }

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static double ReadNumber(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;

    /// <summary>把快照 JSON 整理成模型可读的文本：地址、可交互元素清单、正文。</summary>
    private static string FormatSnapshot(JsonElement root)
    {
        ThrowIfError(root);

        var builder = new StringBuilder();
        builder.Append("地址：").Append(ReadString(root, "url")).Append('\n');
        builder.Append("标题：").Append(ReadString(root, "title")).Append('\n');
        builder.Append("加载状态：").Append(ReadString(root, "ready")).Append('\n');

        var total = root.TryGetProperty("count", out var count) && count.ValueKind == JsonValueKind.Number
            ? count.GetInt32()
            : 0;
        builder.Append("\n可交互元素 ").Append(total).Append(" 个：\n");

        if (root.TryGetProperty("elements", out var elements) && elements.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in elements.EnumerateArray())
            {
                // 只输出真正的引用编号（e0、e1…），不加 [1] 这类前导序号：
                // 两套编号并存时模型会把序号当成引用传给 click/type，然后收到「元素引用不合法」。
                builder.Append(ReadString(element, "ref")).Append(' ').Append(ReadString(element, "tag"));

                var input = ReadString(element, "input");
                if (!string.IsNullOrEmpty(input)) builder.Append('(').Append(input).Append(')');

                var text = ReadString(element, "text");
                builder.Append(string.IsNullOrEmpty(text) ? "（无文字）" : "「" + text + "」");

                var value = ReadString(element, "value");
                if (!string.IsNullOrEmpty(value)) builder.Append(" 当前值：").Append(value);
                builder.Append('\n');
            }
        }

        var body = ReadString(root, "text");
        if (!string.IsNullOrEmpty(body))
            builder.Append("\n正文：\n").Append(body);

        return builder.ToString().TrimEnd();
    }

    /// <summary>页面状态轻量查询：点击/输入后回报当前地址、标题与加载状态。</summary>
    private const string PageStateScript =
        "{ url: location.href, title: document.title, ready: document.readyState }";

    /// <summary>
    /// 页面快照：给可交互元素打上 data-sp-ref 引用编号（点击/输入靠它定位），
    /// 并带上正文文本，让模型既能看清能点什么、也能直接读内容。
    /// </summary>
    private static readonly string SnapshotScript = $@"
(function () {{
  var selector = 'a[href],button,input,textarea,select,summary,[role=button],[role=link],[contenteditable=true]';
  var nodes = document.querySelectorAll(selector);
  var items = [];
  var index = 0;
  for (var i = 0; i < nodes.length; i++) {{
    if (index >= {MaxElements}) break;
    var el = nodes[i];
    var rect = el.getBoundingClientRect();
    if (rect.width <= 0 && rect.height <= 0) continue;
    var text = (el.innerText || '').trim();
    if (!text) text = String(el.value || '').trim();
    if (!text) text = (el.getAttribute('placeholder') || '').trim();
    if (!text) text = (el.getAttribute('aria-label') || '').trim();
    if (!text) text = (el.getAttribute('title') || '').trim();
    var reference = 'e' + index;
    index++;
    try {{ el.setAttribute('data-sp-ref', reference); }} catch (e) {{ }}
    items.push({{
      ref: reference,
      tag: el.tagName.toLowerCase(),
      input: el.getAttribute('type') || '',
      text: text.slice(0, 60),
      value: String(el.value || '').slice(0, 60)
    }});
  }}
  var body = document.body && document.body.innerText ? document.body.innerText : '';
  return {{
    url: location.href,
    title: document.title,
    ready: document.readyState,
    count: items.length,
    elements: items,
    text: body.slice(0, {MaxPageText})
  }};
}})()";

    /// <summary>引用编号只允许 e 加数字，避免把任意字符串拼进脚本。</summary>
    private static string ValidateReference(string reference)
    {
        var value = reference?.Trim() ?? string.Empty;
        if (!ReferencePattern.IsMatch(value))
            throw new ArgumentException(
                $"元素引用不合法：{reference}。引用来自 browser_snapshot 返回的 e0、e1 这类编号。");
        return value;
    }

    /// <summary>
    /// 网址规范化：没写协议时补 https；只放行 http/https，
    /// file://、javascript: 这类地址不进浏览器（内置浏览器不应成为本地文件通道）。
    /// </summary>
    private static Uri NormalizeUrl(string url)
    {
        var text = url?.Trim() ?? string.Empty;
        if (text.Length == 0) throw new ArgumentException("网址不能为空。");

        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            throw new ArgumentException($"不是合法的网址：{url}");

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException($"只支持 http/https 网址，收到的是 {uri.Scheme}。");
        return uri;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BrowserService));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var window = _window;
        _window = null;
        if (window is null) return;

        // 退出路径上不能在这里等 UI 线程，交给消息泵收尾
        try
        {
            Dispatcher.UIThread.Post(() => DestroyWindow(window));
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "close browser window failed");
        }
    }
}