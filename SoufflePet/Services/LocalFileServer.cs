// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace SoufflePet.Services;

/// <summary>本地静态文件服务器抽象（WebView 版加载 wwwroot 页面用，DI 注册）。</summary>
public interface ILocalFileServer : IDisposable
{
    /// <summary>启动服务器，返回实际监听端口。已启动时直接返回现有端口。</summary>
    int Start();

    /// <summary>停止服务器（幂等）。</summary>
    void Stop();

    /// <summary>
    /// 为页面登记一个本地文件（图片代理）：token 12 小时有效，只能读取登记过的路径。
    /// 聊天记录里的图片可能在窗口里反复滚入视口，token 太短会在重渲染后 404。
    /// 返回 token，页面拼成 <c>/local-file?token=…</c>。
    /// </summary>
    string RegisterFile(string path);

    /// <summary>为超大 JSON（snapshot / event）登记一次性访问 token，页面拼成 <c>/state?token=…</c>。</summary>
    string RegisterJson(string json);
}

/// <summary>
/// 本地静态文件服务器：HttpListener 从 18923 起扫描可用端口，服务 wwwroot 目录。
/// 额外提供 /local-file 与 /state 两个短期 token 通道，避免把任意本地路径暴露给页面。
/// 单例懒创建——原生模式且未打开 React UI 时不占端口。
/// </summary>
public sealed class LocalFileServer : ILocalFileServer
{
    private static readonly ILogger Logger = Log.ForContext<LocalFileServer>();

    private static readonly string WwwRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    private const int Port = 18923;
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FileTokenLifetime = TimeSpan.FromHours(12);

    private readonly FileTokenStore _tokens = new();
    private readonly Lock _gate = new();

    private HttpListener? _listener;
    private int _port;

    public int Start()
    {
        lock (_gate)
        {
            if (_listener != null) return _port;

            for (var port = Port; port < Port + 10; port++)
            {
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://localhost:{port}/");
                try
                {
                    listener.Start();
                }
                catch (HttpListenerException)
                {
                    listener.Close();
                    continue;
                }

                _listener = listener;
                _port = port;
                _ = ServeAsync(listener);
                Logger.Information("static server started port={Port} root={Root}", port, WwwRoot);
                return port;
            }
        }

        throw new InvalidOperationException("无法启动本地静态服务器，端口 18923-18932 均被占用");
    }

    public void Stop()
    {
        HttpListener? listener;
        lock (_gate)
        {
            listener = _listener;
            _listener = null;
        }

        try { listener?.Stop(); } catch { /* ignore */ }
        try { listener?.Close(); } catch { /* ignore */ }
        _tokens.Clear();
    }

    public void Dispose() => Stop();

    public string RegisterFile(string path) => _tokens.RegisterFile(path, FileTokenLifetime);

    public string RegisterJson(string json) => _tokens.RegisterJson(json, TokenLifetime);

    private async Task ServeAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch
            {
                return; // listener 已停止
            }

            try
            {
                await HandleAsync(ctx).ConfigureAwait(false);
            }
            catch
            {
                try { ctx.Response.Abort(); } catch { /* ignore */ }
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        if (path.StartsWith("/local-file", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/state", StringComparison.OrdinalIgnoreCase))
        {
            await ServeTokenAsync(ctx, path).ConfigureAwait(false);
            return;
        }

        // 模型文件名是中文，URL 中被百分号编码，必须解码；根路径/目录回退到 index.html
        var rel = Uri.UnescapeDataString(path.TrimStart('/'));
        if (rel.Length == 0 || rel.EndsWith('/')) rel += "index.html";
        var file = Path.GetFullPath(Path.Combine(WwwRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(WwwRoot);
        if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = FileTokenStore.ContentTypeFor(file);
        ApplyCommonHeaders(ctx.Response);
        if (rel.StartsWith("app/", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("app\\", StringComparison.OrdinalIgnoreCase))
            ApplyAppSecurityHeaders(ctx.Response);
        if (rel.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            Logger.Information("serving page {Rel}", rel);

        ctx.Response.ContentLength64 = new FileInfo(file).Length;
        await using (var fs = File.OpenRead(file))
        {
            await fs.CopyToAsync(ctx.Response.OutputStream).ConfigureAwait(false);
        }

        ctx.Response.Close();
    }

    private async Task ServeTokenAsync(HttpListenerContext ctx, string path)
    {
        var token = ctx.Request.QueryString["token"];
        var entry = _tokens.Take(token);
        if (entry is null)
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        byte[] bytes;
        if (entry.Bytes is { } memory)
        {
            bytes = memory;
        }
        else if (entry.FilePath is { } filePath && File.Exists(filePath))
        {
            bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
        }
        else
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = entry.ContentType;
        ApplyCommonHeaders(ctx.Response);
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();

        // /state 是快照/事件的短期载体；读走即可释放，/local-file 允许页面重复取图。
        if (path.StartsWith("/state", StringComparison.OrdinalIgnoreCase)) _tokens.Consume(token!);
    }

    private static void ApplyCommonHeaders(HttpListenerResponse response)
    {
        // 本地页面/图片都不缓存：改了 CSS 或重新生成 token 后必须立刻生效。
        response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        response.Headers["Pragma"] = "no-cache";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
    }

    private static void ApplyAppSecurityHeaders(HttpListenerResponse response)
    {
        response.Headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
            "img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self'; " +
            "object-src 'none'; base-uri 'self'; form-action 'none'; frame-ancestors 'none'";
    }
}
