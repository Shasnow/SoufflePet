using System;
using System.IO;
using System.Net;
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
}

/// <summary>
/// 本地静态文件服务器：HttpListener 从 18923 起扫描可用端口，服务 wwwroot 目录。
/// 单例懒创建——原生模式（默认）下永不被解析，不占端口。
/// </summary>
public sealed class LocalFileServer : ILocalFileServer
{
    private static readonly ILogger Logger = Log.ForContext<LocalFileServer>();

    private static readonly string WwwRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    private const int Port = 18923;

    private HttpListener? _listener;
    private int _port;

    public int Start()
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
        throw new InvalidOperationException("无法启动本地静态服务器，端口 18923-18932 均被占用");
    }

    public void Stop()
    {
        try { _listener?.Stop(); } catch { /* ignore */ }
        try { _listener?.Close(); } catch { /* ignore */ }
        _listener = null;
    }

    public void Dispose() => Stop();

    private static async Task ServeAsync(HttpListener listener)
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
                // 模型文件名是中文，URL 中被百分号编码，必须解码；根路径回退到 index.html
                var rel = Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath.TrimStart('/'));
                if (rel.Length == 0) rel = "index.html";
                var file = Path.GetFullPath(Path.Combine(WwwRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
                var root = Path.GetFullPath(WwwRoot);
                if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    continue;
                }

                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = Path.GetExtension(file).ToLowerInvariant() switch
                {
                    ".html" => "text/html; charset=utf-8",
                    ".js" => "text/javascript; charset=utf-8",
                    ".json" => "application/json; charset=utf-8",
                    ".png" => "image/png",
                    ".ico" => "image/x-icon",
                    _ => "application/octet-stream", // .moc3 等
                };
                await using (var fs = File.OpenRead(file))
                {
                    await fs.CopyToAsync(ctx.Response.OutputStream);
                }
                ctx.Response.Close();
            }
            catch
            {
                try { ctx.Response.Abort(); } catch { /* ignore */ }
            }
        }
    }
}
