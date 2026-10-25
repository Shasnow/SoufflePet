// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using SoufflePet.Models;
using SoufflePet.Services;

namespace SoufflePet.ViewModels;

/// <summary>
/// MCP 服务器设置窗口的视图模型：左侧服务器列表 + 右侧编辑表单。
/// 保存/删除/启用切换把改动写入家目录下的 <c>.souffle/mcp.json</c>（见 <see cref="IMcpConfigStore"/>）；
/// 随后通知 <see cref="IMcpToolService"/> 断开旧连接，下一次对话按新配置重连。
/// </summary>
public partial class McpSettingsViewModel : ViewModelBase
{
    private static readonly ILogger Logger = Log.ForContext<McpSettingsViewModel>();

    private readonly SouffleSettings _settings;
    private readonly IMcpConfigStore _configStore;
    private readonly IMcpToolService _mcp;
    private readonly McpToolCache _toolCache;

    /// <summary>当前订阅了 PropertyChanged 的选中项（换选中时先退订）。</summary>
    private McpServerItemViewModel? _subscribedServer;

    public McpSettingsViewModel(
        SouffleSettings settings,
        IMcpConfigStore configStore,
        IMcpToolService mcp,
        McpToolCache toolCache)
    {
        _settings = settings;
        _configStore = configStore;
        _mcp = mcp;
        _toolCache = toolCache;
        Reload();
    }

    /// <summary>服务器列表（每次打开窗口时按设置文件重建）。</summary>
    public ObservableCollection<McpServerItemViewModel> Servers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectedTools))]
    [NotifyPropertyChangedFor(nameof(SelectedToolStatus))]
    public partial McpServerItemViewModel? SelectedServer { get; set; }

    public bool HasSelection => SelectedServer is not null;

    /// <summary>选中服务器的工具列表（未选中时为空，避免绑定到 null 子属性报错）。</summary>
    public IReadOnlyList<McpToolInfo> SelectedTools => SelectedServer?.Tools ?? [];

    /// <summary>选中服务器的工具状态文案（未选中时为空串）。</summary>
    public string SelectedToolStatus => SelectedServer?.ToolStatus ?? string.Empty;

    /// <summary>宠物称呼（设置项）。</summary>
    private string PetName => _settings.PetName;

    /// <summary>窗口标题。</summary>
    public string WindowTitle => $"{PetName} · MCP 服务器";

    /// <summary>标题栏下方的说明文案。</summary>
    public string ToolHint => $"配置后，服务器上的工具会交给{PetName}调用";

    /// <summary>配置文件路径（左侧列表下方的提示与错误信息用）。</summary>
    public string ConfigPath => _configStore.DisplayPath;

    /// <summary>读写配置文件失败时的说明（如文件被占用、格式损坏）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConfigError))]
    public partial string? ConfigError { get; set; }

    public bool HasConfigError => !string.IsNullOrEmpty(ConfigError);

    /// <summary>按当前配置文件重建列表（窗口每次显示前调用，保证与文件一致）。</summary>
    public void Reload()
    {
        foreach (var item in Servers) item.EnabledChanged -= OnItemEnabledChanged;
        Servers.Clear();

        var configs = _configStore.Load();
        ConfigError = _configStore.LastError;
        foreach (var config in configs)
        {
            var item = new McpServerItemViewModel(config);

            // 上次启动时获取过的工具直接展示，不必等重新连接
            var cached = _toolCache.Get(config.Name);
            if (cached.Count > 0) item.SetTools(cached, $"上次获取：{cached.Count} 个工具");

            item.EnabledChanged += OnItemEnabledChanged;
            Servers.Add(item);
        }

        SelectedServer = Servers.FirstOrDefault();
    }

    partial void OnSelectedServerChanged(McpServerItemViewModel? value)
    {
        if (_subscribedServer is not null) _subscribedServer.PropertyChanged -= OnSelectedServerPropertyChanged;
        _subscribedServer = value;
        if (_subscribedServer is not null) _subscribedServer.PropertyChanged += OnSelectedServerPropertyChanged;

        OnPropertyChanged(nameof(SelectedTools));
        OnPropertyChanged(nameof(SelectedToolStatus));

        EditorError = null;
        if (value is null)
        {
            LoadEditor(new McpServerConfig());
            return;
        }

        LoadEditor(value.Config);
    }

    /// <summary>工具状态变化时同步到视图模型上的投影属性。</summary>
    private void OnSelectedServerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(McpServerItemViewModel.ToolStatus))
            OnPropertyChanged(nameof(SelectedToolStatus));
    }

    // ===== 编辑表单：字段是配置的副本，点「保存」才写回 =====

    [ObservableProperty]
    public partial string EditorName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStdioTransport))]
    [NotifyPropertyChangedFor(nameof(IsHttpTransport))]
    private partial McpTransportType EditorTransport { get; set; }

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
        _toolCache.Remove(item.Config.Name);
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
    private async Task SaveServerAsync()
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
        var previousName = config.Name;
        config.Name = name;
        // 缓存以服务器名为键，改名后旧条目作废（随后重新获取会写入新键）
        if (!string.Equals(previousName, name, StringComparison.OrdinalIgnoreCase)) _toolCache.Remove(previousName);
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

        // 保存后立即连接一次，让用户当场看到该服务器能提供哪些工具
        if (item.Enabled) await LoadToolsAsync();
    }

    /// <summary>连接当前选中的服务器，列出全部工具并在界面上展示（用完即断开）。</summary>
    [RelayCommand]
    private async Task LoadToolsAsync()
    {
        if (SelectedServer is not { } item) return;

        item.IsLoadingTools = true;
        item.ToolStatus = "正在连接…";
        try
        {
            var tools = await _mcp.GetServerToolsAsync(item.Config, CancellationToken.None);
            _toolCache.Set(item.Config.Name, tools);
            item.SetTools(tools, tools.Count == 0 ? "连接成功，但没有可用工具" : $"已连接，共 {tools.Count} 个工具");
        }
        catch (OperationCanceledException)
        {
            item.SetTools([], "连接超时或已取消");
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "MCP server {Server} probe failed", item.Name);
            item.SetTools([], $"连接失败：{ex.Message}");
        }
        finally
        {
            item.IsLoadingTools = false;
        }
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
    /// 把列表写回 mcp.json，并让 MCP 服务丢弃旧连接，下一次对话按新配置重连。
    /// 写入失败时把原因显示在窗口里（例如文件被其它程序占用）。
    /// </summary>
    private void Persist()
    {
        try
        {
            _configStore.Save([.. Servers.Select(s => s.Config)]);
            ConfigError = null;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "save MCP config failed");
            ConfigError = $"保存 {_configStore.DisplayPath} 失败：{ex.Message}";
            return;
        }

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
