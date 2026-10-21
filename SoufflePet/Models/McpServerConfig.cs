// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace SoufflePet.Models;

/// <summary>MCP 服务器的传输方式（JSON 中以字符串读写）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum McpTransportType
{
    /// <summary>本地子进程，通过标准输入输出通信。</summary>
    Stdio,

    /// <summary>远程服务，通过 Streamable HTTP / SSE 通信。</summary>
    Http,
}

/// <summary>
/// 一个 MCP 服务器的配置。列表写在 SoufflePet.json 的 McpServers 中，
/// 启用的服务器会在首次对话时连接，其工具以「服务器名__工具名」暴露给模型。
/// </summary>
public class McpServerConfig
{
    [Description("服务器名称（工具名以此为前缀，建议用英文，如 files）")]
    public string Name { get; set; } = string.Empty;

    [Description("是否启用（修改后需重启应用生效）")]
    public bool Enabled { get; set; } = true;

    [Description("传输方式：Stdio（本地子进程）或 Http（远程服务）")]
    public McpTransportType Transport { get; set; } = McpTransportType.Stdio;

    [Description("Stdio：启动命令，例如 npx、python 或可执行文件路径")]
    public string Command { get; set; } = string.Empty;

    [Description("Stdio：命令参数，例如 [\"-y\", \"@modelcontextprotocol/server-filesystem\", \"D:\\\\\"]")]
    public List<string> Arguments { get; set; } = [];

    [Description("Stdio：子进程工作目录，留空表示应用目录")]
    public string WorkingDirectory { get; set; } = string.Empty;

    [Description("Stdio：追加的环境变量，例如 {\"API_KEY\": \"...\"}")]
    public Dictionary<string, string> Environment { get; set; } = [];

    [Description("Http：服务地址，例如 http://localhost:3000/mcp")]
    public string Url { get; set; } = string.Empty;

    [Description("Http：附加请求头，例如 {\"Authorization\": \"Bearer ...\"}")]
    public Dictionary<string, string> Headers { get; set; } = [];

    [Description("连接与列出工具的超时时间（秒）")]
    public int TimeoutSeconds { get; set; } = 30;
}
