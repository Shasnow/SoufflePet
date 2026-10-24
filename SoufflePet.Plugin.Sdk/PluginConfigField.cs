// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SoufflePet.Plugin.Sdk;

/// <summary>
/// plugin.json 中 <c>configSchema</c> 的一项：描述插件配置表单里的一个控件。
/// 值本身仍存在 <c>config</c> 中，宿主按 <see cref="Key" /> 读写。
/// </summary>
public sealed class PluginConfigField
{
    /// <summary>支持的表单类型。</summary>
    public static readonly IReadOnlySet<string> SupportedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "text",
        "multiline",
        "number",
        "boolean",
        "select",
        "list",
    };

    /// <summary>对应 <c>config</c> 对象的属性名。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>表单标签；为空时用 <see cref="Key" />。</summary>
    public string? Label { get; set; }

    /// <summary>控件下方的说明文字。</summary>
    public string? Description { get; set; }

    /// <summary>控件类型：text / multiline / number / boolean / select / list，默认 text。</summary>
    public string Type { get; set; } = "text";

    /// <summary>select 的可选项。</summary>
    public List<string>? Options { get; set; }

    [JsonIgnore]
    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? Key : Label;
}
