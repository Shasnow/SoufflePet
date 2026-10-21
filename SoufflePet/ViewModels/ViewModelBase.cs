// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using CommunityToolkit.Mvvm.ComponentModel;

namespace SoufflePet.ViewModels;

/// <summary>
/// 视图模型基类（同时也是 ViewLocator 的匹配依据）：
/// 继承 ObservableObject 提供 INotifyPropertyChanged 实现，属性变更通知交给 MVVM Toolkit。
/// </summary>
public abstract class ViewModelBase : ObservableObject
{
}
