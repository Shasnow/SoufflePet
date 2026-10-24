// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using SoufflePet.Plugin.Sdk;

namespace SoufflePet.ViewModels;

/// <summary>
/// 插件配置表单的一项：把 <see cref="PluginConfigField" /> 描述和 config 里的当前值
/// 转成界面可直接绑定的属性，保存时再转回 JSON。
/// </summary>
public partial class PluginConfigFieldViewModel : ObservableObject
{
    public PluginConfigFieldViewModel(PluginConfigField field, JsonElement? value)
    {
        Key = field.Key;
        Label = field.DisplayLabel;
        Description = field.Description ?? string.Empty;
        Kind = PluginConfigField.SupportedTypes.Contains(field.Type)
            ? field.Type.ToLowerInvariant()
            : "text";

        if (IsSelect)
        {
            var options = field.Options?
                .Where(option => !string.IsNullOrWhiteSpace(option))
                .ToList() ?? [];
            var current = value is { ValueKind: JsonValueKind.String } ? value.Value.GetString() : null;
            if (!string.IsNullOrEmpty(current) && !options.Contains(current))
                options.Insert(0, current);

            Options = options;
            Text = current ?? options.FirstOrDefault() ?? string.Empty;
        }
        else if (IsBool)
        {
            Bool = value is { ValueKind: JsonValueKind.True };
        }
        else if (IsList)
        {
            if (value is { ValueKind: JsonValueKind.Array } array)
            {
                Text = string.Join('\n', array.EnumerateArray().Select(item =>
                    item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString()));
            }
        }
        else if (value is { } currentValue)
        {
            Text = currentValue.ValueKind switch
            {
                JsonValueKind.String => currentValue.GetString() ?? string.Empty,
                JsonValueKind.Number => currentValue.GetRawText(),
                _ => string.Empty,
            };
        }
    }

    /// <summary>config 对象里的属性名。</summary>
    public string Key { get; }

    public string Label { get; }

    public string Description { get; }

    public bool HasDescription => Description.Length > 0;

    /// <summary>标准化后的类型：text / multiline / number / boolean / select / list。</summary>
    public string Kind { get; }

    public bool IsMultiline => Kind == "multiline";

    public bool IsList => Kind == "list";

    public bool IsBool => Kind == "boolean";

    public bool IsSelect => Kind == "select";

    /// <summary>text 与 number 共用一个单行输入框。</summary>
    public bool IsText => !IsMultiline && !IsList && !IsBool && !IsSelect;

    public IReadOnlyList<string> Options { get; } = [];

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Bool { get; set; }

    /// <summary>把当前表单值转成 JSON；数字格式错误时抛出 <see cref="FormatException" />。</summary>
    public JsonNode? ToJson()
    {
        if (IsBool) return JsonValue.Create(Bool);

        if (Kind == "number")
        {
            if (!double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                throw new FormatException($"「{Label}」需要填写数字");
            return JsonValue.Create(number);
        }

        if (IsList)
        {
            var array = new JsonArray();
            foreach (var item in Text.Split(
                         ['\n', '\r'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                array.Add(JsonValue.Create(item));
            }

            return array;
        }

        return JsonValue.Create(Text);
    }

    /// <summary>按清单里的 configSchema 创建全部表单项；没有 schema 时返回空列表。</summary>
    public static IReadOnlyList<PluginConfigFieldViewModel> CreateAll(PluginManifest manifest)
    {
        if (manifest.ConfigSchema is not { Count: > 0 } schema) return [];

        return
        [
            .. schema
                .Where(field => !string.IsNullOrWhiteSpace(field.Key))
                .Select(field => new PluginConfigFieldViewModel(field, Read(manifest.Config, field.Key)))
        ];
    }

    private static JsonElement? Read(JsonElement? config, string key) =>
        config is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty(key, out var value)
            ? value
            : null;
}
