// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using SoufflePet.Models;

namespace SoufflePet.ViewModels;

/// <summary>
/// MCP 服务器列表项：包装配置对象，只把列表上会直接改动的字段（启用/名称）
/// 暴露成可观察属性，其余字段由右侧编辑表单整体保存。
/// </summary>
public partial class McpServerItemViewModel(McpServerConfig config) : ObservableObject
{
    /// <summary>被包装的配置（与 SoufflePet.json 中的条目是同一对象）。</summary>
    public McpServerConfig Config { get; } = config;

    /// <summary>启用状态被用户切换（需要立即持久化并重连）。</summary>
    public event EventHandler? EnabledChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial bool Enabled { get; set; } = config.Enabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial string Name { get; set; } = config.Name;

    /// <summary>列表项第二行的简介：传输方式 + 命令/地址。</summary>
    public string Summary => Config.Transport == McpTransportType.Http
        ? $"Http · {Config.Url}"
        : $"Stdio · {Config.Command}";

    partial void OnEnabledChanged(bool value)
    {
        Config.Enabled = value;
        EnabledChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>编辑保存后刷新列表显示（简介依赖 Config 中非可观察的字段）。</summary>
    public void Refresh()
    {
        Name = Config.Name;
        Enabled = Config.Enabled;
        OnPropertyChanged(nameof(Summary));
    }
}
