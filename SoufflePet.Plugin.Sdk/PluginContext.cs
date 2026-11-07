// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SoufflePet.Plugin.Sdk;

/// <summary>宿主提供给插件的运行环境：由宿主在加载时构造，插件只读使用。</summary>
public sealed class PluginContext(
    string pluginId,
    string pluginDirectory,
    string dataDirectory,
    JsonElement? configuration,
    ILogger logger)
{
    /// <summary>插件 id（取自 plugin.json）。</summary>
    public string PluginId { get; } = pluginId;

    /// <summary>插件所在目录的绝对路径。</summary>
    public string PluginDirectory { get; } = pluginDirectory;

    /// <summary>插件可写的用户数据目录（~/.souffle/plugin-data/&lt;id&gt;），用于缓存等运行期数据；目录由插件按需创建。</summary>
    public string DataDirectory { get; } = dataDirectory;

    /// <summary>插件配置：默认值来自 plugin.json 的 config 段，用户保存后由 ~/.souffle/plugin-configs/&lt;id&gt;.json 覆盖。</summary>
    public JsonElement? Configuration { get; } = configuration;

    /// <summary>已按插件 id 分类的日志出口，转发到应用日志。</summary>
    public ILogger Logger { get; } = logger;
}
