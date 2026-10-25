// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using Serilog;
using SoufflePet.Models;

namespace SoufflePet.Services;

/// <summary>设置界面展示用的一条 MCP 工具（工具信息缓存也用它）。</summary>
public sealed record McpToolInfo(string Name, string Description)
{
    [JsonIgnore]
    public bool HasDescription => Description.Length > 0;
}

/// <summary>向对话提供 MCP 服务器上的工具。</summary>
public interface IMcpToolService
{
    /// <summary>
    /// 返回所有已配置且连接成功的 MCP 工具（首次调用时逐个连接服务器并缓存结果）。
    /// 单个服务器失败只记日志、不影响其它服务器，不向调用方抛出网络/进程错误；
    /// 仅当取消时抛 <see cref="OperationCanceledException"/>。
    /// </summary>
    Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 连接指定服务器并列出其全部工具，用完立即断开（供设置界面即时查看，不影响对话用的连接缓存）。
    /// 配置不完整、连接失败或超时以异常抛出，由调用方展示。
    /// </summary>
    Task<IReadOnlyList<McpToolInfo>> GetServerToolsAsync(
        McpServerConfig server,
        CancellationToken cancellationToken);

    /// <summary>
    /// 断开所有连接并清空工具缓存，下一次 <see cref="GetToolsAsync"/> 按最新配置重新连接。
    /// 设置界面保存后调用，使改动无需重启即可生效。
    /// </summary>
    Task ResetAsync();
}

/// <summary>
/// MCP 客户端管理：按家目录下的 <c>.souffle/mcp.json</c>（见 <see cref="IMcpConfigStore"/>）懒连接各个服务器，
/// 缓存工具列表，并把工具名统一改成「服务器名__工具名」以避免跨服务器重名。
/// </summary>
public sealed class McpToolService(IMcpConfigStore configStore) : IMcpToolService, IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<McpToolService>();

    /// <summary>多数模型接口对工具名长度的限制是 64 个字符。</summary>
    private const int MaxToolNameLength = 64;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<McpClient> _clients = [];
    private IReadOnlyList<AITool>? _tools;
    private bool _disposed;

    public async Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        if (_tools is { } cached) return cached;

        // 懒连接只做一次：并发请求（如多个对话窗口同时发消息）在这里排队
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _tools ??= await ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetAsync()
    {
        if (_disposed) return;

        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return; // 应用退出期间容器已释放
        }

        try
        {
            if (_disposed) return;
            foreach (var client in _clients) await DisposeQuietlyAsync(client).ConfigureAwait(false);
            _clients.Clear();
            _tools = null;
            Logger.Debug("MCP connections reset");
        }
        finally
        {
            try { _gate.Release(); } catch (ObjectDisposedException) { }
        }
    }

    public async Task<IReadOnlyList<McpToolInfo>> GetServerToolsAsync(
        McpServerConfig server,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured(server))
            throw new InvalidOperationException("配置不完整，请先填写启动命令或服务地址");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, server.TimeoutSeconds)));

        await using var client = await McpClient.CreateAsync(
                CreateTransport(server), cancellationToken: timeout.Token)
            .ConfigureAwait(false);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);

        Logger.Debug("MCP server {Server} probed tools={Tools}", server.Name, tools.Count);
        return [.. tools.Select(tool => new McpToolInfo(tool.Name, tool.Description))];
    }

    private async Task<IReadOnlyList<AITool>> ConnectAsync(CancellationToken cancellationToken)
    {
        var servers = configStore.Load()
            .Where(server => server.Enabled && !string.IsNullOrWhiteSpace(server.Name) && IsConfigured(server))
            .ToList();
        if (servers.Count == 0) return [];

        var tools = new List<AITool>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in servers)
            await AddServerToolsAsync(server, tools, usedNames, cancellationToken).ConfigureAwait(false);

        Logger.Debug("MCP tools loaded servers={Servers} tools={Tools}", servers.Count, tools.Count);
        return tools;
    }

    private async Task AddServerToolsAsync(
        McpServerConfig server,
        List<AITool> tools,
        HashSet<string> usedNames,
        CancellationToken cancellationToken)
    {
        McpClient? client = null;
        IClientTransport? transport = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, server.TimeoutSeconds)));

            transport = CreateTransport(server);
            client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token).ConfigureAwait(false);
            var serverTools = await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);

            // 连接成功：交给 _clients 统一释放，finally 不再处理
            _clients.Add(client);
            client = null;
            transport = null;

            foreach (var tool in serverTools)
                tools.Add(tool.WithName(UniqueToolName(server.Name, tool.Name, usedNames)));

            Logger.Debug("MCP server {Server} connected tools={Tools}", server.Name, serverTools.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            Logger.Warning(ex, "MCP server {Server} timed out after {Seconds}s", server.Name, server.TimeoutSeconds);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "MCP server {Server} unavailable", server.Name);
        }
        finally
        {
            if (client is not null) await DisposeQuietlyAsync(client).ConfigureAwait(false);
            else if (transport is IAsyncDisposable disposable) await DisposeQuietlyAsync(disposable).ConfigureAwait(false);
        }
    }

    /// <summary>尚未填写完整的新条目跳过连接，避免空命令/空地址产生一次必然失败的连接。</summary>
    private static bool IsConfigured(McpServerConfig server) =>
        server.Transport == McpTransportType.Http
            ? Uri.TryCreate(server.Url.Trim(), UriKind.Absolute, out var uri)
              && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            : !string.IsNullOrWhiteSpace(server.Command);

    private static IClientTransport CreateTransport(McpServerConfig server) =>
        server.Transport == McpTransportType.Http
            ? new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(server.Url.Trim()),
                Name = server.Name,
                TransportMode = HttpTransportMode.AutoDetect,
                AdditionalHeaders = server.Headers,
            })
            : new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.Name,
                Command = server.Command.Trim(),
                Arguments = server.Arguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(server.WorkingDirectory)
                    ? Environment.CurrentDirectory
                    : server.WorkingDirectory,
                InheritEnvironmentVariables = true,
                EnvironmentVariables = server.Environment.ToDictionary(kv => kv.Key, kv => (string?)kv.Value),
                StandardErrorLines = line => Logger.Debug("MCP {Server} stderr: {Line}", server.Name, line),
            });

    /// <summary>工具名加服务器前缀并做 ASCII 净化（含中文/空格等字符的服务器名或工具名会变成下划线）。</summary>
    private static string UniqueToolName(string serverName, string toolName, HashSet<string> usedNames)
    {
        var raw = $"{SanitizeName(serverName, "server")}__{SanitizeName(toolName, "tool")}";
        if (raw.Length > MaxToolNameLength) raw = raw[..MaxToolNameLength];

        var name = raw;
        for (var i = 2; !usedNames.Add(name); i++)
        {
            var suffix = $"_{i}";
            name = raw.Length + suffix.Length <= MaxToolNameLength
                ? raw + suffix
                : raw[..(MaxToolNameLength - suffix.Length)] + suffix;
        }

        return name;
    }

    private static string SanitizeName(string value, string fallback)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');

        var name = builder.ToString().Trim('_');
        return name.Length == 0 ? fallback : name;
    }

    private static async Task DisposeQuietlyAsync(IAsyncDisposable disposable)
    {
        try
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Dispose MCP resource failed");
        }
    }

    /// <summary>
    /// 退出时关闭所有 MCP 连接（stdio 服务器会随之结束子进程）。DI 容器是同步释放的，
    /// 这里对每个连接限时等待，避免个别服务器卡住应用退出。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var client in _clients)
        {
            try
            {
                client.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Dispose MCP client failed");
            }
        }

        _clients.Clear();
        _gate.Dispose();
    }
}
