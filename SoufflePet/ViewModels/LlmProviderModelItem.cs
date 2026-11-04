// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoufflePet.Models;

namespace SoufflePet.ViewModels;

/// <summary>
/// 提供商模型清单里的一行（工作副本）：勾选状态在点「保存配置」时才写回
/// <see cref="ChatProviderConfig.Models" />，因此未保存的勾选不会影响对话窗口。
/// </summary>
public sealed partial class LlmProviderModelItem(ChatProviderModel model, LlmModelsViewModel owner) : ObservableObject
{
    public string ModelId { get; } = model.ModelId;

    /// <summary>显示名称（界面上不编辑，保留配置里的值以便手动改 JSON）。</summary>
    public string Name { get; } = model.Name;

    /// <summary>是否加入对话窗口的模型选择列表。</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; } = model.IsActive;

    /// <summary>从清单里删掉这一行（保存后才会真正从配置里消失）。</summary>
    [RelayCommand]
    private void Remove() => owner.RemoveModel(this);

    /// <summary>写回配置用的一份快照。</summary>
    public ChatProviderModel ToConfig() => new()
    {
        ModelId = ModelId,
        Name = Name,
        IsActive = IsActive
    };
}
