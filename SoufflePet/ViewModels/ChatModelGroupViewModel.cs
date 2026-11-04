// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.Collections.ObjectModel;

namespace SoufflePet.ViewModels;

/// <summary>
/// 模型选择浮层里的一个提供商分组：组标题是提供商名称，组内是它下面已启用的模型。
/// </summary>
public sealed class ChatModelGroupViewModel(string provider)
{
    /// <summary>分组标题（提供商名称；未填提供商的模型归到「其他」）。</summary>
    public string Provider { get; } = provider.Length > 0 ? provider : "其他";

    public ObservableCollection<ChatModelPickerItem> Models { get; } = [];
}
