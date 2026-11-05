// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using SoufflePet.Models;

namespace SoufflePet.Services;

/// <summary>
/// MCP 连接配置的读写：单独存放在家目录的 <c>.souffle/mcp.json</c>，不再写入 SoufflePet.json。
/// 文件用通行的 MCP 配置格式 <c>{"mcpServers": {"名称": {...}}}</c>，
/// 可直接抄 VS Code / Claude 等客户端里的配置；本应用不认识的键在保存时原样保留。
/// </summary>
public interface IMcpConfigStore
{
    /// <summary>配置文件完整路径。</summary>
    string FilePath { get; }

    /// <summary>界面上展示用的路径，家目录部分写成 <c>~/</c>。</summary>
    string DisplayPath { get; }

    /// <summary>最近一次读写失败的说明；成功或文件尚不存在时为 null。</summary>
    string? LastError { get; }

    /// <summary>读取全部服务器配置（保持文件中的顺序）；文件不存在返回空列表。</summary>
    IReadOnlyList<McpServerConfig> Load();

    /// <summary>整体覆盖写入；列表中没有的服务器会被删除。写入失败时抛异常。</summary>
    void Save(IReadOnlyList<McpServerConfig> servers);
}

/// <summary>mcp.json 的读写实现，见 <see cref="IMcpConfigStore"/>。</summary>
public sealed class McpConfigStore : IMcpConfigStore
{
    private static readonly ILogger Logger = Log.ForContext<McpConfigStore>();

    private const int DefaultTimeoutSeconds = 30;

    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public McpConfigStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".souffle", "mcp.json");
        DisplayPath = ToDisplayPath(FilePath);
    }

    public string FilePath { get; }

    public string DisplayPath { get; }

    public string? LastError { get; private set; }

    public IReadOnlyList<McpServerConfig> Load()
    {
        LastError = null;
        if (!File.Exists(FilePath)) return [];

        try
        {
            var text = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(text)) return [];

            var document = JsonSerializer.Deserialize<McpConfigDocument>(text, JsonSerializerOptions);
            if (document?.McpServers is not { Count: > 0 } servers) return [];

            var result = new List<McpServerConfig>(servers.Count);
            // 服务器名用于工具名前缀、也用于界面判重，统一按忽略大小写处理
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, entry) in servers)
            {
                if (entry is null || string.IsNullOrWhiteSpace(name)) continue;

                var trimmed = name.Trim();
                if (!seenNames.Add(trimmed))
                {
                    Logger.Warning(
                        "MCP config {Path} has duplicate server name {Name} (names are case-insensitive); ignored",
                        FilePath, trimmed);
                    continue;
                }

                result.Add(ToConfig(trimmed, entry));
            }

            return result;
        }
        catch (Exception ex)
        {
            LastError = $"读取 {DisplayPath} 失败：{ex.Message}";
            Logger.Warning(ex, "Failed to load MCP config {Path}", FilePath);
            return [];
        }
    }

    public void Save(IReadOnlyList<McpServerConfig> servers)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var previous = ReadDocumentQuietly();
        var document = new McpConfigDocument
        {
            Extra = previous?.Extra,
            McpServers = new Dictionary<string, McpServerEntry>(StringComparer.OrdinalIgnoreCase),
        };
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var server in servers)
        {
            var name = server.Name.Trim();
            if (name.Length == 0) continue;
            if (!seenNames.Add(name))
            {
                // 名称忽略大小写判重（与 Load / FindPrevious 一致），否则 rider 与 Rider 会同时写入
                Logger.Warning("Duplicate MCP server name {Name} ignored while saving", name);
                continue;
            }

            document.McpServers[name] = ToEntry(server, previous?.McpServers);
        }

        File.WriteAllText(FilePath, JsonSerializer.Serialize(document, JsonSerializerOptions));
        LastError = null;
        Logger.Debug("MCP config saved servers={Servers} path={Path}", document.McpServers.Count, FilePath);
    }

    /// <summary>读取现有文件以保留未知字段；失败（含尚无文件）时返回 null。</summary>
    private McpConfigDocument? ReadDocumentQuietly()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var text = File.ReadAllText(FilePath);
            return string.IsNullOrWhiteSpace(text)
                ? null
                : JsonSerializer.Deserialize<McpConfigDocument>(text, JsonSerializerOptions);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Ignoring unreadable MCP config {Path}", FilePath);
            return null;
        }
    }

    private McpServerConfig ToConfig(string name, McpServerEntry entry) => new()
    {
        Name = name,
        Enabled = entry.Enabled ?? true,
        Transport = ResolveTransport(entry),
        Command = entry.Command?.Trim() ?? string.Empty,
        Arguments = entry.Args is null ? [] : [.. entry.Args.Where(arg => !string.IsNullOrWhiteSpace(arg))],
        WorkingDirectory = entry.Cwd?.Trim() ?? string.Empty,
        Environment = ToMap(name, "env", entry.Env),
        Url = entry.Url?.Trim() ?? string.Empty,
        Headers = ToMap(name, "headers", entry.Headers),
        TimeoutSeconds = entry.Timeout is > 0 and <= 600 ? entry.Timeout.Value : DefaultTimeoutSeconds,
    };

    private static McpServerEntry ToEntry(
        McpServerConfig server, Dictionary<string, McpServerEntry>? previousEntries)
    {
        var stdio = server.Transport == McpTransportType.Stdio;
        return new McpServerEntry
        {
            Type = stdio ? "stdio" : "http",
            Command = stdio ? server.Command.Trim() : null,
            Args = stdio && server.Arguments.Count > 0 ? [.. server.Arguments] : null,
            Cwd = stdio && server.WorkingDirectory.Length > 0 ? server.WorkingDirectory.Trim() : null,
            Env = stdio && server.Environment.Count > 0 ? ToElements(server.Environment) : null,
            Url = stdio ? null : server.Url.Trim(),
            Headers = !stdio && server.Headers.Count > 0 ? ToElements(server.Headers) : null,
            // 默认启用：省略 enabled，保持文件干净且与其它客户端的配置一致
            Enabled = server.Enabled ? null : false,
            Timeout = server.TimeoutSeconds == DefaultTimeoutSeconds ? null : server.TimeoutSeconds,
            Extra = FindPrevious(previousEntries, server.Name)?.Extra,
        };
    }

    private static McpServerEntry? FindPrevious(
        Dictionary<string, McpServerEntry>? entries, string name) =>
        entries?.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>type 缺省或无法识别时按 command / url 判断，兼容各客户端导出的配置。</summary>
    private static McpTransportType ResolveTransport(McpServerEntry entry) =>
        entry.Type?.Trim().ToLowerInvariant() switch
        {
            "stdio" => McpTransportType.Stdio,
            "http" or "sse" or "streamable-http" or "streamablehttp" => McpTransportType.Http,
            _ => string.IsNullOrWhiteSpace(entry.Command) && !string.IsNullOrWhiteSpace(entry.Url)
                ? McpTransportType.Http
                : McpTransportType.Stdio,
        };

    /// <summary>
    /// env / headers 只支持字符串标量（数字、布尔转成文本）；对象或数组无法传给子进程/HTTP 头，
    /// 丢弃并记一条警告，避免用户以为配置生效了。
    /// </summary>
    private Dictionary<string, string> ToMap(string serverName, string field, Dictionary<string, JsonElement>? source)
    {
        if (source is null) return [];

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;

            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    map[key] = value.GetString() ?? string.Empty;
                    break;
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    map[key] = value.GetRawText();
                    break;
                default:
                    Logger.Warning(
                        "MCP server {Server}: {Field}.{Key} is {Kind}, only scalar values are supported; ignored",
                        serverName, field, key, value.ValueKind);
                    break;
            }
        }

        return map;
    }

    private static Dictionary<string, JsonElement> ToElements(Dictionary<string, string> map) =>
        map.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value));

    private static string ToDisplayPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return path;

        var relative = Path.GetRelativePath(home, path);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal)) return path;
        return "~/" + relative.Replace('\\', '/');
    }

    /// <summary>mcp.json 顶层结构。</summary>
    private sealed class McpConfigDocument
    {
        [JsonPropertyName("mcpServers")]
        public Dictionary<string, McpServerEntry>? McpServers { get; set; }

        /// <summary>其它客户端写入的顶层键，原样保留。</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    /// <summary>一个服务器的标准 MCP 配置项（未知键原样保留）。</summary>
    private sealed class McpServerEntry
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("command")]
        public string? Command { get; set; }

        [JsonPropertyName("args")]
        public List<string>? Args { get; set; }

        [JsonPropertyName("env")]
        public Dictionary<string, JsonElement>? Env { get; set; }

        [JsonPropertyName("cwd")]
        public string? Cwd { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("headers")]
        public Dictionary<string, JsonElement>? Headers { get; set; }

        [JsonPropertyName("enabled")]
        public bool? Enabled { get; set; }

        [JsonPropertyName("timeout")]
        public int? Timeout { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }
}
