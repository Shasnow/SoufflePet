// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SoufflePet.Plugin.Sdk;

/// <summary>宿主提供给插件的运行环境：由宿主在加载时构造，插件只读使用。</summary>
public sealed class PluginContext(
    string pluginId,
    string pluginDirectory,
    JsonElement? configuration,
    ILogger logger)
{
    /// <summary>插件 id（取自 plugin.json）。</summary>
    public string PluginId { get; } = pluginId;

    /// <summary>插件所在目录的绝对路径。</summary>
    public string PluginDirectory { get; } = pluginDirectory;

    /// <summary>plugin.json 里的 config 段；未声明时为 null。</summary>
    public JsonElement? Configuration { get; } = configuration;

    /// <summary>已按插件 id 分类的日志出口，转发到应用日志。</summary>
    public ILogger Logger { get; } = logger;
}
