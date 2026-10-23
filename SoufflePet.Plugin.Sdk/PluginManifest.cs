// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoufflePet.Plugin.Sdk;

/// <summary>
/// 插件清单（插件目录下的 plugin.json）。宿主只按这里的声明发现与加载插件，
/// 插件不需要（也无法）注册到主程序的任何代码里。
/// </summary>
public sealed class PluginManifest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>插件唯一标识：字母、数字、<c>-</c>、<c>_</c>，用于启用状态与工具名冲突时的前缀。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>展示名称；为空时用 <see cref="Id" />。</summary>
    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = "1.0.0";

    public string Author { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>入口程序集文件名（相对插件目录），例如 <c>MyPlugin.dll</c>。</summary>
    public string Assembly { get; set; } = string.Empty;

    /// <summary>入口类型全名；省略时取程序集内唯一的 <see cref="IPlugin" /> 实现。</summary>
    public string? EntryType { get; set; }

    /// <summary>单个工具调用的超时（秒），宿主限制在 1–300，默认 30。</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>插件自定义配置：原样交给 <see cref="PluginContext.Configuration" />。</summary>
    public JsonElement? Config { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    /// <summary>读取并解析 plugin.json；文件缺失或 JSON 非法时抛出异常，由宿主统一转成「加载失败」。</summary>
    public static PluginManifest Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>解析 plugin.json 文本；JSON 非法时抛出异常。</summary>
    public static PluginManifest Parse(string json) =>
        JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions)
        ?? throw new InvalidDataException("plugin.json 内容为空");

    /// <summary>校验必填字段；返回错误文案，null 表示通过。</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Id)) return "plugin.json 缺少 id";
        if (!Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return $"插件 id「{Id}」只能包含字母、数字、- 和 _";
        if (string.IsNullOrWhiteSpace(Assembly)) return "plugin.json 缺少 assembly";
        if (Path.IsPathRooted(Assembly) || Assembly.Split('/', '\\').Any(part => part == ".."))
            return "assembly 必须是插件目录内的相对路径";
        return null;
    }
}
