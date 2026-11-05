// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.Collections.Generic;
using SoufflePet.Plugin.Sdk;

namespace SoufflePet.Services.Plugins;

/// <summary>插件生命周期状态。</summary>
public enum PluginState
{
    /// <summary>已发现但尚未加载程序集。</summary>
    Discovered,

    /// <summary>正在创建加载上下文并初始化。</summary>
    Loading,

    /// <summary>已加载并可用，工具会随对话提供给模型。</summary>
    Enabled,

    /// <summary>用户停用（程序集已卸载或从未加载）。</summary>
    Disabled,

    /// <summary>加载或初始化失败；原因见 <see cref="PluginDescriptor.Error"/>。</summary>
    Failed,
}

/// <summary>一个已发现插件的运行时描述：清单来自磁盘，状态由宿主维护，界面只读。</summary>
public sealed class PluginDescriptor
{
    public required PluginManifest Manifest { get; init; }

    /// <summary>插件目录绝对路径（程序集与 plugin.json 所在目录）。</summary>
    public required string Directory { get; init; }

    /// <summary>true 表示随主程序发布（应用目录 plugins），false 表示用户导入（~/.souffle/plugins）。</summary>
    public bool IsBuiltIn { get; init; }

    /// <summary>用户期望的启用状态（持久化在设置里）。</summary>
    public bool Enabled { get; set; }

    public PluginState State { get; set; }

    /// <summary>加载/初始化失败原因，供界面展示。</summary>
    public string? Error { get; set; }

    /// <summary>插件提供的工具名（原始名称，工具名冲突时实际暴露名会加前缀）。</summary>
    public IReadOnlyList<string> ToolNames { get; set; } = [];

    /// <summary>运行期工具调用失败/超时次数，供界面展示。</summary>
    public int FailureCount { get; set; }
}

/// <summary>导入结果：Message 可直接展示给用户，PluginId 为装好后选中的插件 id（失败时为 null）。</summary>
public readonly record struct PluginInstallResult(bool Success, string Message, string? PluginId = null);
