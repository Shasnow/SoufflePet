// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Serilog;

namespace SoufflePet.Services.Web;

/// <summary>
/// WebBridgeServer 的业务处理接口：构建 snapshot、处理白名单方法、向页面发事件。
/// 实现见 ChatRpc（会话/对话 + 外观 + 窗口命令；设置面板 M5 继续补齐）。
/// </summary>
public interface IWebRpcHandler
{
    /// <summary>窗口角色；M2 固定 app（不再区分 chat/settings）。</summary>
    string Role { get; }

    /// <summary>握手/重同步时的完整快照。</summary>
    AppSnapshotDto BuildSnapshot();

    /// <summary>处理一条白名单请求；返回值会作为 result 回包。</summary>
    Task<object?> HandleAsync(
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken);

    /// <summary>业务层主动推事件；server 负责加 rev、合帧与发送。</summary>
    event Action<string, object?>? EventProduced;
}

/// <summary>可预期的方法错误（映射为页面侧的 error.code）。</summary>
public sealed class BridgeRpcException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// 宿主 → 页面 / 页面 → 宿主的 JSON-RPC 桥（docs/react-ui-contract.md v1）。
/// 页面侧通过 NativeWebView 注入的 invokeCSharpAction 发请求；本类通过 InvokeScript 调
/// window.__bridge.receive。超过 256 KB 的快照/事件改走 LocalFileServer 的 /state token。
/// </summary>
public sealed class WebBridgeServer : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<WebBridgeServer>();

    /// <summary>单条 InvokeScript 的内联上限；超过走 /state。</summary>
    private const int MaxInlineBytes = 256 * 1024;

    private readonly NativeWebView _view;
    private readonly ILocalFileServer _files;
    private readonly IWebRpcHandler _handler;

    /// <summary>写请求串行化：session.open/delete、settings 保存等不能交错执行。</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>窗口生命周期取消源：窗口关闭后停止新请求并取消在途 Core 操作。</summary>
    private readonly CancellationTokenSource _lifetime = new();

    private long _rev;
    private bool _pageReady;
    private bool _disposed;

    public WebBridgeServer(NativeWebView view, ILocalFileServer files, IWebRpcHandler handler)
    {
        _view = view;
        _files = files;
        _handler = handler;
        _view.WebMessageReceived += OnWebMessage;
        _handler.EventProduced += OnEventProduced;
    }

    /// <summary>页面首次发来消息（通常是 hello）时触发；窗口可据此补发状态/聚焦。</summary>
    public event Action? PageReady;

    public bool IsPageReady => _pageReady;

    /// <summary>当前 rev（调试/测试用）。</summary>
    public long Rev => Interlocked.Read(ref _rev);

    public void PublishEvent(string name, object? payload) => SendEvent(name, payload);

    public void PublishTheme(ThemeDto theme) => SendEvent("host.theme", theme);

    public void PublishWindowState(WindowStateDto state) => SendEvent("window.state", state);

    /// <summary>窗口重新显示/主题变化后主动补发快照，避免隐藏期间丢事件。</summary>
    public void RefreshSnapshot()
    {
        // 首次 Show 时页面可能还没开始加载；hello 会自己发快照，这里不抢跑。
        if (_pageReady) SendSnapshot();
    }

    private void OnEventProduced(string name, object? payload)
    {
        try
        {
            SendEvent(name, payload);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "publish bridge event {Name} failed", name);
        }
    }

    private void OnWebMessage(object? sender, WebMessageReceivedEventArgs e)
    {
        if (_disposed || string.IsNullOrWhiteSpace(e.Body)) return;
        if (!_pageReady)
        {
            _pageReady = true;
            try
            {
                PageReady?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "bridge page-ready handler failed");
            }
        }
        _ = HandleMessageAsync(e.Body);
    }

    private async Task HandleMessageAsync(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1)
                return;
            if (!root.TryGetProperty("type", out var type) || !string.Equals(type.GetString(), "req", StringComparison.Ordinal))
                return;
            if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id)) return;
            if (!root.TryGetProperty("method", out var methodElement) || methodElement.GetString() is not { } method) return;

            var parameters = root.TryGetProperty("params", out var parameterElement)
                ? parameterElement.Clone()
                : default;

            if (string.Equals(method, "hello", StringComparison.Ordinal)
                || string.Equals(method, "app.resync", StringComparison.Ordinal))
            {
                Logger.Information("bridge {Method} received", method);
                SendSnapshot();
                return;
            }

            if (IsReadOnly(method))
            {
                await InvokeHandlerAsync(id, method, parameters, _lifetime.Token).ConfigureAwait(false);
                return;
            }

            try
            {
                await _writeGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // 窗口已关闭：不再处理排队请求
            }

            try
            {
                await InvokeHandlerAsync(id, method, parameters, _lifetime.Token).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 窗口关闭/应用退出：页面已不可用，不必回包
        }
        catch (JsonException)
        {
            // 不是合法的桥消息：忽略
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "bridge message handling failed");
        }
    }

    private async Task InvokeHandlerAsync(
        int id,
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        try
        {
            var started = Stopwatch.GetTimestamp();
            var result = await _handler.HandleAsync(method, parameters, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) return;

            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (elapsedMs >= 16)
                Logger.Debug("bridge {Method} took {Elapsed:0.0}ms", method, elapsedMs);
            SendResponse(id, true, result, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Logger.Debug("bridge {Method} canceled by window lifetime", method);
        }
        catch (BridgeRpcException ex)
        {
            SendResponse(id, false, null, new { code = ex.Code, message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            SendResponse(id, false, null, new { code = "invalid_argument", message = ex.Message });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "bridge method {Method} failed", method);
            SendResponse(id, false, null, new { code = "internal", message = "内部错误，详见应用日志" });
        }
    }

    /// <summary>
    /// 只读请求不占写锁：窗口命令与 Core 内部已加锁的会话列表/搜索可即时执行；
    /// fetchModels 只发网络请求、不改设置，避免 30 s 网络等待阻塞其它写请求。
    /// 设置列表/详情仍走写锁：它们会枚举可变的 ChatProviders/任务集合，不能与保存并发。
    /// </summary>
    private static bool IsReadOnly(string method) =>
        method.StartsWith("window.", StringComparison.Ordinal)
        || method is "session.list" or "session.search"
        || method is "settings.llm.fetchModels";

    private void SendResponse(int id, bool ok, object? result, object? error)
    {
        object envelope = ok
            ? new { v = 1, id, ok = true, result }
            : new { v = 1, id, ok = false, error };
        SendInline(envelope);
    }

    private void SendSnapshot()
    {
        try
        {
            var snapshot = _handler.BuildSnapshot();
            var rev = Interlocked.Increment(ref _rev);
            Logger.Debug("bridge snapshot built rev={Rev} sessions={Sessions}", rev, snapshot.Sessions.Count);
            var json = WebJson.Serialize(new { v = 1, type = "snapshot", rev, data = snapshot });

            if (Utf8Size(json) > MaxInlineBytes)
            {
                // /state 里存的是快照本身（不含 envelope）：页面 fetch 后直接 applySnapshot(data, rev)，
                // 与内联 snapshot 的 data 结构保持一致。
                var token = _files.RegisterJson(WebJson.Serialize(snapshot));
                SendInline(new
                {
                    v = 1,
                    type = "snapshot-file",
                    rev,
                    url = $"/state?token={token}",
                });
                return;
            }

            SendInlineJson(json);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "build/send snapshot failed");
        }
    }

    private void SendEvent(string name, object? payload)
    {
        // 首帧前（hello 之前）不发事件：页面还没有 __bridge.receive；快照会在 hello 后补全主题。
        if (!_pageReady || _disposed) return;
        try
        {
            var rev = Interlocked.Increment(ref _rev);
            var node = JsonSerializer.SerializeToNode(payload, WebJson.Options);

            // 对象 payload 继续带 rev（v1 页面按 payload.rev 排序）；同时在 envelope 上带 rev，
            // 这样字符串/数组/null 等非对象 payload 也不会被静默替换成空对象。
            if (node is JsonObject obj) obj["rev"] = rev;

            var envelope = new JsonObject
            {
                ["v"] = 1,
                ["type"] = "event",
                ["name"] = name,
                ["rev"] = rev,
                ["payload"] = node,
            };
            var json = WebJson.Serialize(envelope);

            if (Utf8Size(json) > MaxInlineBytes)
            {
                // 同上：/state 只装 payload，页面按 event-file 的 name/rev 直接分发。
                var token = _files.RegisterJson(node is null ? "null" : WebJson.Serialize(node));
                SendInline(new
                {
                    v = 1,
                    type = "event-file",
                    name,
                    rev,
                    url = $"/state?token={token}",
                });
                return;
            }

            SendInlineJson(json);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "send bridge event {Name} failed", name);
        }
    }

    private static int Utf8Size(string text) => System.Text.Encoding.UTF8.GetByteCount(text);

    private void SendInline(object envelope)
    {
        SendInlineJson(WebJson.Serialize(envelope));
    }

    private void SendInlineJson(string json)
    {
        if (_disposed) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SendInlineJson(json));
            return;
        }

        // JSON 直接作为 JS 对象字面量参数；U+2028/U+2029 在旧 JS 字符串里有换行语义，
        // 这里统一转义，避免 InvokeScript 解析失败。
        var safe = json.Replace("\u2028", "\\u2028", StringComparison.Ordinal)
            .Replace("\u2029", "\\u2029", StringComparison.Ordinal);
        var script = $"window.__bridge && window.__bridge.receive({safe});";
        _ = SendScriptAsync(script);
    }

    private async Task SendScriptAsync(string script)
    {
        try
        {
            await _view.InvokeScript(script);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "InvokeScript failed (page may not be ready)");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _view.WebMessageReceived -= OnWebMessage;
        _handler.EventProduced -= OnEventProduced;
    }
}