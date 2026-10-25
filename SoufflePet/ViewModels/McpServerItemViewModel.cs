// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SoufflePet.Models;
using SoufflePet.Services;

namespace SoufflePet.ViewModels;

/// <summary>
/// MCP 服务器列表项：包装配置对象，只把列表上会直接改动的字段（启用/名称）
/// 暴露成可观察属性，其余字段由右侧编辑表单整体保存。
/// </summary>
public partial class McpServerItemViewModel(McpServerConfig config) : ObservableObject
{
    /// <summary>被包装的配置（与 mcp.json 中的条目是同一对象）。</summary>
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

    /// <summary>最近一次连接获取到的工具（仅用于设置界面展示）。</summary>
    public ObservableCollection<McpToolInfo> Tools { get; } = [];

    [ObservableProperty]
    public partial bool IsLoadingTools { get; set; }

    [ObservableProperty]
    public partial string ToolStatus { get; set; } = "尚未获取工具";

    public void SetTools(IReadOnlyList<McpToolInfo> tools, string status)
    {
        Tools.Clear();
        foreach (var tool in tools) Tools.Add(tool);
        ToolStatus = status;
    }

    /// <summary>编辑保存后刷新列表显示（简介依赖 Config 中非可观察的字段）。</summary>
    public void Refresh()
    {
        Name = Config.Name;
        Enabled = Config.Enabled;
        OnPropertyChanged(nameof(Summary));
    }
}
