// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.Collections.Generic;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SoufflePet.Models;

/// <summary>
/// 一个模型提供商的配置（OpenAI 兼容接口）：名称 + 接口信息 + 该提供商下的模型清单。
/// 只有清单里勾选启用（<see cref="ChatProviderModel.IsActive" />）的模型才会进入对话窗口的模型选择列表，
/// 展开由 <see cref="SouffleSettings.RebuildChatModels" /> 负责。
/// 字段可观察：管理面板的编辑框直接绑定这里，左侧列表随之同步。
/// </summary>
public partial class ChatProviderConfig : ObservableObject
{
    [ObservableProperty]
    [Description("提供商名称（对话窗口模型选择列表的分组标题）")]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    [Description("接口地址（OpenAI 兼容的 base url，例如 https://api.example.com/v1）")]
    public partial string BaseUrl { get; set; } = string.Empty;

    [ObservableProperty]
    [Description("API 密钥（该提供商下的模型共用）")]
    public partial string ApiKey { get; set; } = string.Empty;

    [Description("该提供商下的模型清单：从接口获取或手动添加，勾选的才会出现在对话窗口")]
    public List<ChatProviderModel> Models { get; set; } = [];
}

/// <summary>提供商下的一个模型：接口里的 model 标识 + 是否启用。</summary>
public class ChatProviderModel
{
    [Description("模型标识（接口请求中的 model 字段，例如 deepseek-chat）")]
    public string ModelId { get; set; } = string.Empty;

    [Description("显示名称（对话窗口里看到的名字；留空时用模型标识）")]
    public string Name { get; set; } = string.Empty;

    [Description("是否加入对话窗口的模型选择列表")]
    public bool IsActive { get; set; }
}
