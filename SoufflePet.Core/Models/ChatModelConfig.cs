// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace SoufflePet.Models;

/// <summary>
/// 对话窗口模型选择列表里的一项：由提供商配置展开而来（提供商 / 接口 / 密钥都取自提供商），
/// 不单独落盘，见 <see cref="SouffleSettings.RebuildChatModels" />。
/// </summary>
public class ChatModelConfig
{
    [Description("提供商（模型选择列表的分组标题）")]
    public string Provider { get; set; } = string.Empty;

    [Description("显示名称（对话窗口模型列表中的名字）")]
    public string Name { get; set; } = string.Empty;

    [Description("模型标识（接口请求中的 model 字段）")]
    public string ModelId { get; set; } = string.Empty;

    [Description("接口地址（OpenAI 兼容的 base url）")]
    public string BaseUrl { get; set; } = string.Empty;

    [Description("API 密钥")]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>提供商 + 模型标识组成的稳定键：重建列表与恢复上次选中项时用它对应同一个模型。</summary>
    [JsonIgnore]
    public string Key => KeyOf(Provider, ModelId);

    /// <summary>键里提供商与模型标识的分隔符：两者都可能含空格，用换行分隔避免歧义。</summary>
    private const char KeySeparator = '\n';

    /// <summary>拼接提供商与模型标识。</summary>
    public static string KeyOf(string provider, string modelId) => $"{provider}{KeySeparator}{modelId}";

    /// <summary>
    /// 把键里的提供商换成新名字（提供商改名后同步选中项用）；键不属于该提供商时原样返回。
    /// </summary>
    public static string RenameProvider(string key, string previousProvider, string provider)
    {
        var separator = key.IndexOf(KeySeparator);
        if (separator < 0 || !string.Equals(key[..separator], previousProvider, StringComparison.Ordinal))
            return key;

        return KeyOf(provider, key[(separator + 1)..]);
    }
}
