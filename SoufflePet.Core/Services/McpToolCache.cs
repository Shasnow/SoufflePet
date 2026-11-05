// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using Serilog;

namespace SoufflePet.Services;

/// <summary>
/// MCP 工具列表缓存（mcp-tools.json）：保存每个服务器上次获取到的工具，
/// 程序重启后设置界面直接展示，无需重新连接；重新获取或保存服务器时覆盖。
/// </summary>
public sealed class McpToolCache
{
    private static readonly ILogger Logger = Log.ForContext<McpToolCache>();
    private static readonly string CacheFilePath = Path.Combine(AppContext.BaseDirectory, "mcp-tools.json");
    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly Dictionary<string, List<McpToolInfo>> _servers;

    private McpToolCache(Dictionary<string, List<McpToolInfo>> servers) => _servers = servers;

    public static McpToolCache Load()
    {
        if (!File.Exists(CacheFilePath)) return new McpToolCache([]);

        try
        {
            var servers = JsonSerializer.Deserialize<Dictionary<string, List<McpToolInfo>>>(
                File.ReadAllText(CacheFilePath), JsonSerializerOptions);
            return new McpToolCache(servers ?? []);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to load MCP tool cache");
            return new McpToolCache([]);
        }
    }

    /// <summary>取出某个服务器缓存的工具；没有缓存时返回空列表。</summary>
    public IReadOnlyList<McpToolInfo> Get(string serverName) =>
        _servers.TryGetValue(serverName, out var tools) ? tools : [];

    public void Set(string serverName, IReadOnlyList<McpToolInfo> tools)
    {
        _servers[serverName] = [.. tools];
        Save();
    }

    public void Remove(string serverName)
    {
        if (_servers.Remove(serverName)) Save();
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(CacheFilePath, JsonSerializer.Serialize(_servers, JsonSerializerOptions));
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to save MCP tool cache");
        }
    }
}
