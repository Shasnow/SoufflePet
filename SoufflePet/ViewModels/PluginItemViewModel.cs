// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoufflePet.Services.Plugins;

namespace SoufflePet.ViewModels;

/// <summary>
/// 插件列表项：展示字段在刷新时从 descriptor 复制一份，避免后台加载线程改写状态时界面读到半更新数据。
/// </summary>
public partial class PluginItemViewModel : ObservableObject
{
    public PluginItemViewModel(PluginDescriptor descriptor)
    {
        Descriptor = descriptor;
        Enabled = descriptor.Enabled;
        ConfigFields = PluginConfigFieldViewModel.CreateAll(descriptor.Manifest);
    }

    /// <summary>启用开关被用户切换（需要立即写回设置并加载/卸载）。</summary>
    public event EventHandler? EnabledChanged;

    /// <summary>用户点击「保存配置」，由外层视图模型写回 plugin.json。</summary>
    public event EventHandler? ConfigSaveRequested;

    public PluginDescriptor Descriptor { get; }

    public string Id => Descriptor.Manifest.Id;

    public string Name => Descriptor.Manifest.DisplayName;

    public string Version => $"v{Descriptor.Manifest.Version}";

    public string Author => Descriptor.Manifest.Author;

    public bool HasAuthor => Author.Length > 0;

    public string Description => Descriptor.Manifest.Description;

    public bool HasDescription => Description.Length > 0;

    /// <summary>来源：随主程序发布或用户导入。</summary>
    public string Source => Descriptor.IsBuiltIn ? "内置" : "用户导入";

    public string Directory => Descriptor.Directory;

    /// <summary>左侧列表第二行：id · 版本。</summary>
    public string Subtitle => $"{Id} · {Version}";

    /// <summary>右侧状态：已启用（N 个工具）/ 已禁用 / 加载失败：原因。</summary>
    public string StateText => Descriptor.State switch
    {
        PluginState.Enabled => $"已启用 · {Descriptor.ToolNames.Count} 个工具",
        PluginState.Disabled => "已禁用",
        PluginState.Loading => "正在加载…",
        PluginState.Discovered => "已启用 · 等待加载",
        PluginState.Failed => $"加载失败：{Descriptor.Error}",
        _ => Descriptor.State.ToString(),
    };

    /// <summary>运行期失败次数提示，为 0 时不显示。</summary>
    public string FailureText => Descriptor.FailureCount > 0 ? $"最近失败 {Descriptor.FailureCount} 次" : string.Empty;

    public bool HasFailure => Descriptor.FailureCount > 0;

    /// <summary>工具清单（每行一个，冲突时实际暴露名可能带插件前缀）。</summary>
    public string ToolSummary => Descriptor.State switch
    {
        PluginState.Discovered or PluginState.Loading => "正在加载工具…",
        _ when Descriptor.ToolNames.Count == 0 => "该插件没有提供工具",
        _ => string.Join('\n', Descriptor.ToolNames.Select(name => $"· {name}")),
    };

    /// <summary>配置表单；清单未声明 configSchema 时为空。</summary>
    public IReadOnlyList<PluginConfigFieldViewModel> ConfigFields { get; }

    public bool HasConfig => ConfigFields.Count > 0;

    /// <summary>把表单当前值组装成 config 对象，供保存。</summary>
    public JsonElement BuildConfig()
    {
        var config = new JsonObject();
        foreach (var field in ConfigFields) config[field.Key] = field.ToJson();

        using var document = JsonDocument.Parse(config.ToJsonString());
        return document.RootElement.Clone();
    }

    [RelayCommand]
    private void SaveConfig() => ConfigSaveRequested?.Invoke(this, EventArgs.Empty);

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    partial void OnEnabledChanged(bool value) => EnabledChanged?.Invoke(this, EventArgs.Empty);
}
