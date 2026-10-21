// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.ComponentModel;

namespace SoufflePet.Models;

/// <summary>
/// 一个 LLM 对话模型的配置（名称 + 接口信息）。
/// 目前只用于对话窗口的模型选择与持久化，尚未真正发起请求。
/// </summary>
public class ChatModelConfig
{
    [Description("显示名称（对话窗口模型列表中的名字）")]
    public string Name { get; set; } = string.Empty;

    [Description("模型标识（接口请求中的 model 字段）")]
    public string ModelId { get; set; } = string.Empty;

    [Description("接口地址（OpenAI 兼容的 base url）")]
    public string BaseUrl { get; set; } = string.Empty;

    [Description("API 密钥")]
    public string ApiKey { get; set; } = string.Empty;
}
