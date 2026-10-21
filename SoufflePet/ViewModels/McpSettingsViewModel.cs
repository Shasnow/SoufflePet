// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SoufflePet.Models;
using SoufflePet.Services;

namespace SoufflePet.ViewModels;

/// <summary>
/// MCP 服务器设置窗口的视图模型：左侧服务器列表 + 右侧编辑表单。
/// 保存/删除/启用切换只改 <see cref="SouffleSettings.McpServers"/>，持久化由 App 组合根
/// 统一处理；随后通知 <see cref="IMcpToolService"/> 断开旧连接，下一次对话按新配置重连。
/// </summary>
public partial class McpSettingsViewModel : ViewModelBase
{
    private static readonly ILogger Logger = Log.ForContext<McpSettingsViewModel>();

    private readonly SouffleSettings _settings;
    private readonly IMcpToolService _mcp;

    public McpSettingsViewModel(SouffleSettings settings, IMcpToolService mcp)
    {
        _settings = settings;
        _mcp = mcp;
        Reload();
    }

    /// <summary>服务器列表（每次打开窗口时按设置文件重建）。</summary>
    public ObservableCollection<McpServerItemViewModel> Servers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial McpServerItemViewModel? SelectedServer { get; set; }

    public bool HasSelection => SelectedServer is not null;

    /// <summary>按当前设置重建列表（窗口每次显示前调用，保证与设置文件一致）。</summary>
    public void Reload()
    {
        foreach (var item in Servers) item.EnabledChanged -= OnItemEnabledChanged;
        Servers.Clear();

        foreach (var config in _settings.McpServers)
        {
            var item = new McpServerItemViewModel(config);
            item.EnabledChanged += OnItemEnabledChanged;
            Servers.Add(item);
        }

        SelectedServer = Servers.FirstOrDefault();
    }

    partial void OnSelectedServerChanged(McpServerItemViewModel? value)
    {
        EditorError = null;
        if (value is null)
        {
            LoadEditor(new McpServerConfig());
            return;
        }

        LoadEditor(value.Config);
    }

    // ===== 编辑表单：字段是配置的副本，点「保存」才写回 =====

    [ObservableProperty]
    public partial string EditorName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStdioTransport))]
    [NotifyPropertyChangedFor(nameof(IsHttpTransport))]
    public partial McpTransportType EditorTransport { get; set; }

    public bool IsStdioTransport => EditorTransport == McpTransportType.Stdio;

    public bool IsHttpTransport => EditorTransport == McpTransportType.Http;

    [ObservableProperty]
    public partial string EditorCommand { get; set; } = string.Empty;

    /// <summary>命令参数：每行一个。</summary>
    [ObservableProperty]
    public partial string EditorArguments { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditorWorkingDirectory { get; set; } = string.Empty;

    /// <summary>环境变量：每行 KEY=VALUE。</summary>
    [ObservableProperty]
    public partial string EditorEnvironment { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditorUrl { get; set; } = string.Empty;

    /// <summary>附加请求头：每行 KEY=VALUE。</summary>
    [ObservableProperty]
    public partial string EditorHeaders { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditorTimeout { get; set; } = "30";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditorError))]
    public partial string? EditorError { get; set; }

    public bool HasEditorError => !string.IsNullOrEmpty(EditorError);

    [RelayCommand]
    private void SelectStdio() => EditorTransport = McpTransportType.Stdio;

    [RelayCommand]
    private void SelectHttp() => EditorTransport = McpTransportType.Http;

    [RelayCommand]
    private void AddServer()
    {
        var config = new McpServerConfig
        {
            Name = UniqueName("mcp"),
            Enabled = true, // 新条目默认启用；配置未填全时连接层会跳过，不会产生失败日志
        };

        var item = new McpServerItemViewModel(config);
        item.EnabledChanged += OnItemEnabledChanged;
        Servers.Add(item);
        SelectedServer = item;
        Persist();
    }

    [RelayCommand]
    private void DeleteServer()
    {
        if (SelectedServer is not { } item) return;

        item.EnabledChanged -= OnItemEnabledChanged;
        Servers.Remove(item);
        SelectedServer = Servers.FirstOrDefault();
        Persist();
    }

    [RelayCommand]
    private void RevertServer()
    {
        if (SelectedServer is not { } item) return;
        EditorError = null;
        LoadEditor(item.Config);
    }

    [RelayCommand]
    private void SaveServer()
    {
        if (SelectedServer is not { } item) return;

        var name = EditorName.Trim();
        if (name.Length == 0)
        {
            EditorError = "请填写服务器名称";
            return;
        }

        if (Servers.Any(s => s != item && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            EditorError = "已存在同名服务器";
            return;
        }

        if (!int.TryParse(EditorTimeout.Trim(), out var timeout) || timeout is < 1 or > 600)
        {
            EditorError = "超时时间需为 1-600 之间的整数";
            return;
        }

        var command = EditorCommand.Trim();
        if (EditorTransport == McpTransportType.Stdio && command.Length == 0)
        {
            EditorError = "Stdio 服务器需要填写启动命令";
            return;
        }

        var url = EditorUrl.Trim();
        if (EditorTransport == McpTransportType.Http && !IsHttpUrl(url))
        {
            EditorError = "Http 服务器需要填写合法的 http/https 地址";
            return;
        }

        if (!TryParseMap(EditorEnvironment, out var environment))
        {
            EditorError = "环境变量格式应为每行 KEY=VALUE";
            return;
        }

        if (!TryParseMap(EditorHeaders, out var headers))
        {
            EditorError = "请求头格式应为每行 KEY=VALUE";
            return;
        }

        var config = item.Config;
        config.Name = name;
        config.Transport = EditorTransport;
        config.Command = command;
        config.Arguments = SplitLines(EditorArguments);
        config.WorkingDirectory = EditorWorkingDirectory.Trim();
        config.Environment = environment;
        config.Url = url;
        config.Headers = headers;
        config.TimeoutSeconds = timeout;

        item.Refresh();
        EditorError = null;
        Persist();

        // 名称可能被改过，按新配置重载表单（Trim 等规范化后的值也同步回界面）
        LoadEditor(config);
    }

    private void LoadEditor(McpServerConfig config)
    {
        EditorName = config.Name;
        EditorTransport = config.Transport;
        EditorCommand = config.Command;
        EditorArguments = string.Join(Environment.NewLine, config.Arguments);
        EditorWorkingDirectory = config.WorkingDirectory;
        EditorEnvironment = FormatMap(config.Environment);
        EditorUrl = config.Url;
        EditorHeaders = FormatMap(config.Headers);
        EditorTimeout = config.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
    }

    private void OnItemEnabledChanged(object? sender, EventArgs e) => Persist();

    /// <summary>
    /// 把列表写回设置（新 List 触发 PropertyChanged，由 App 组合根统一保存），
    /// 并让 MCP 服务丢弃旧连接，下一次对话按新配置重连。
    /// </summary>
    private void Persist()
    {
        _settings.McpServers = [.. Servers.Select(s => s.Config)];
        _ = ResetMcpAsync();
    }

    private async Task ResetMcpAsync()
    {
        try
        {
            await _mcp.ResetAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "reset MCP connections failed");
        }
    }

    private string UniqueName(string baseName)
    {
        var name = baseName;
        for (var i = 2; Servers.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
            name = $"{baseName}-{i}";
        return name;
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static List<string> SplitLines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string FormatMap(Dictionary<string, string> map) =>
        string.Join(Environment.NewLine, map.Select(kv => $"{kv.Key}={kv.Value}"));

    /// <summary>按每行 KEY=VALUE 解析（值里可以再出现等号）。</summary>
    private static bool TryParseMap(string text, out Dictionary<string, string> map)
    {
        map = [];
        foreach (var line in SplitLines(text))
        {
            var index = line.IndexOf('=');
            if (index <= 0) return false;

            var key = line[..index].Trim();
            if (key.Length == 0) return false;
            map[key] = line[(index + 1)..].Trim();
        }

        return true;
    }
}
