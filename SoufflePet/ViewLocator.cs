// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using SoufflePet.ViewModels;

namespace SoufflePet;

/// <summary>
/// 视图定位器（在 App.axaml 的 DataTemplates 中注册）：按命名约定把视图模型映射到视图，
/// SoufflePet.ViewModels.XxxViewModel → SoufflePet.Views.XxxView。
/// 这样窗口只放一个 ContentControl 绑定 DataContext，无需在 XAML 里硬编码具体视图。
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null) return null;

        var name = param.GetType().FullName!
            .Replace("ViewModels", "Views", StringComparison.Ordinal)
            .Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);
        if (type is null) return new TextBlock { Text = $"未找到视图：{name}" };

        return (Control)Activator.CreateInstance(type)!;
    }

    public bool Match(object? data) => data is ViewModelBase;
}
