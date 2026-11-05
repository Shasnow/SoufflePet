// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SoufflePet.Core.Chat;
using SoufflePet.Models;
using SoufflePet.Plugin.Sdk;
using SoufflePet.Services.Plugins;

namespace SoufflePet.Services.Web;

/// <summary>
/// 七个设置面板的宿主 API：LLM 提供商、MCP、技能、插件、定时任务、Live2D 模型与外观。
/// 全部走 Core 服务，返回 v1 契约 DTO；文件/目录选择由 AppWebWindow 提供。
/// </summary>
public sealed class SettingsApi
{
    private static readonly ILogger Logger = Log.ForContext<SettingsApi>();

    private readonly IAppWindowCommands _window;
    private readonly SouffleSettings _settings;
    private readonly ThemeService _theme;
    private readonly ILlmModelCatalog _catalog;
    private readonly IMcpConfigStore _mcpConfig;
    private readonly IMcpToolService _mcpTools;
    private readonly ISkillService _skills;
    private readonly IPluginService _plugins;
    private readonly IScheduledTaskService _scheduler;
    private readonly ILive2DModelService _models;
    private readonly IUiDispatcher _ui;

    public SettingsApi(
        IAppWindowCommands window,
        SouffleSettings settings,
        ThemeService theme,
        ILlmModelCatalog catalog,
        IMcpConfigStore mcpConfig,
        IMcpToolService mcpTools,
        ISkillService skills,
        IPluginService plugins,
        IScheduledTaskService scheduler,
        ILive2DModelService models,
        IUiDispatcher ui)
    {
        _window = window;
        _settings = settings;
        _theme = theme;
        _catalog = catalog;
        _mcpConfig = mcpConfig;
        _mcpTools = mcpTools;
        _skills = skills;
        _plugins = plugins;
        _scheduler = scheduler;
        _models = models;
        _ui = ui;
    }

    public async Task<object?> HandleAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        try
        {
            return method switch
            {
                "settings.llm.list" => LlmList(),
                "settings.llm.saveProvider" => LlmSaveProvider(Read<ProviderInputDto>(parameters)),
                "settings.llm.deleteProvider" => LlmDeleteProvider(RequireString(parameters, "name")),
                "settings.llm.fetchModels" => await LlmFetchModelsAsync(parameters, cancellationToken),
                "settings.llm.setEnabled" => LlmSetEnabled(parameters),
                "settings.llm.setDefault" => LlmSetDefault(RequireString(parameters, "key")),

                "settings.mcp.list" => McpList(),
                "settings.mcp.save" => await McpSaveAsync(Read<McpServerDto>(parameters), cancellationToken),
                "settings.mcp.delete" => await McpDeleteAsync(RequireString(parameters, "name"), cancellationToken),
                "settings.mcp.test" => await McpTestAsync(Read<McpServerDto>(parameters), cancellationToken),
                "settings.mcp.refreshTools" => await McpRefreshAsync(RequireString(parameters, "name"), cancellationToken),

                "settings.skills.list" => SkillsList(),
                "settings.skills.setEnabled" => SkillsSetEnabled(parameters),
                "settings.skills.openFolder" => SkillsOpenFolder(RequireString(parameters, "path")),
                "settings.skills.detail" => SkillsDetail(RequireString(parameters, "name")),

                "settings.plugins.list" => PluginsList(),
                "settings.plugins.setEnabled" => await PluginsSetEnabledAsync(parameters, cancellationToken),
                "settings.plugins.getConfigSchema" => PluginsConfigSchema(RequireString(parameters, "id")),
                "settings.plugins.saveConfig" => await PluginsSaveConfigAsync(parameters, cancellationToken),
                "settings.plugins.import" => await PluginsImportAsync(cancellationToken),

                "settings.tasks.list" => TasksList(),
                "settings.tasks.save" => TasksSave(Read<TaskInputDto>(parameters)),
                "settings.tasks.delete" => TasksDelete(RequireString(parameters, "id")),
                "settings.tasks.setEnabled" => TasksSetEnabled(parameters),
                "settings.tasks.runNow" => TasksRunNow(RequireString(parameters, "id")),
                "settings.tasks.nextRun" => TasksNextRun(RequireString(parameters, "id")),
                "settings.tasks.clearLogs" => TasksClearLogs(),

                "settings.models.list" => ModelsList(),
                "settings.models.importFolder" => await ModelsImportAsync(folder: true, cancellationToken),
                "settings.models.importArchive" => await ModelsImportAsync(folder: false, cancellationToken),
                "settings.models.remove" => ModelsRemove(GetInt(parameters, "index")),
                "settings.models.setActive" => ModelsSetActive(GetInt(parameters, "index")),
                "settings.models.validate" => ModelsValidate(GetInt(parameters, "index")),

                "settings.appearance.get" => _theme.GetTheme(),
                "settings.appearance.setAccent" => _theme.SetAccent(RequireString(parameters, "accent")),
                "settings.appearance.setFollowSystem" => _theme.SetFollowSystem(GetBool(parameters, "enabled")),
                "settings.appearance.reset" => _theme.Reset(),
                "settings.appearance.preview" => _theme.Preview(RequireString(parameters, "accent")),
                "settings.appearance.setSidebarCollapsed" => SetSidebarCollapsed(GetBool(parameters, "collapsed")),

                _ => throw new BridgeRpcException("unknown_method", $"未知设置方法 {method}"),
            };
        }
        catch (ChatSessionException ex)
        {
            throw new BridgeRpcException(ex.Code, ex.Message);
        }
    }

    // ===== 对话模型 =====

    private object LlmList() => new
    {
        providers = _settings.ChatProviders.Select(MapProvider).ToList(),
        enabledModels = _settings.ChatModels.Select(model =>
            new ChatModelDto(model.Key, model.Provider, model.Name, model.ModelId)).ToList(),
        defaultKey = string.IsNullOrWhiteSpace(_settings.ChatModelKey) ? null : _settings.ChatModelKey,
    };

    private ProviderDto LlmSaveProvider(ProviderInputDto input)
    {
        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length == 0) throw new BridgeRpcException("invalid_argument", "请填写提供商名称");
        if (!IsHttpUrl(input.BaseUrl)) throw new BridgeRpcException("invalid_argument", "接口地址需为合法的 http/https 地址");

        var previous = _settings.ChatProviders.FirstOrDefault(provider =>
            string.Equals(provider.Name, string.IsNullOrWhiteSpace(input.PreviousName) ? name : input.PreviousName,
                StringComparison.OrdinalIgnoreCase));

        if (_settings.ChatProviders.Any(provider => provider != previous
                && string.Equals(provider.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
            throw new BridgeRpcException("conflict", "已存在同名提供商");

        var previousName = previous?.Name ?? string.Empty;
        if (previous is null)
        {
            previous = new ChatProviderConfig();
            _settings.ChatProviders.Add(previous);
        }

        previous.Name = name;
        previous.BaseUrl = input.BaseUrl.Trim();
        previous.ApiKey = (input.ApiKey ?? string.Empty).Trim();
        previous.Models = [.. (input.Models ?? [])
            .Where(model => !string.IsNullOrWhiteSpace(model.ModelId))
            .Select(model => new ChatProviderModel
            {
                ModelId = model.ModelId.Trim(),
                Name = (model.Name ?? string.Empty).Trim(),
                IsActive = model.IsActive,
            })];

        _settings.ChatModelKey = ChatModelConfig.RenameProvider(_settings.ChatModelKey, previousName, name);
        _settings.RebuildChatModels();
        _settings.Save();
        return MapProvider(previous);
    }

    private object LlmDeleteProvider(string name)
    {
        var provider = _settings.ChatProviders.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (provider is null) throw new BridgeRpcException("not_found", "提供商不存在");

        _settings.ChatProviders.Remove(provider);
        _settings.RebuildChatModels();
        _settings.Save();
        return new { providers = _settings.ChatProviders.Select(MapProvider).ToList() };
    }

    private async Task<object> LlmFetchModelsAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var baseUrl = RequireString(parameters, "baseUrl").Trim();
        if (!IsHttpUrl(baseUrl)) throw new BridgeRpcException("invalid_argument", "接口地址需为合法的 http/https 地址");

        try
        {
            var ids = await _catalog.ListModelsAsync(
                baseUrl,
                GetString(parameters, "apiKey") ?? string.Empty,
                cancellationToken).ConfigureAwait(false);
            return new { modelIds = ids };
        }
        catch (OperationCanceledException)
        {
            throw new BridgeRpcException("timeout", "获取模型列表超时");
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "fetch model list failed");
            throw new BridgeRpcException("timeout", $"获取模型列表失败：{ex.Message}");
        }
    }

    private ProviderDto LlmSetEnabled(JsonElement parameters)
    {
        var providerName = RequireString(parameters, "provider");
        var modelId = RequireString(parameters, "modelId");
        var enabled = GetBool(parameters, "enabled");

        var provider = _settings.ChatProviders.FirstOrDefault(item =>
            string.Equals(item.Name, providerName, StringComparison.OrdinalIgnoreCase))
            ?? throw new BridgeRpcException("not_found", "提供商不存在");
        var model = provider.Models.FirstOrDefault(item =>
            string.Equals(item.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new BridgeRpcException("not_found", "模型不存在");

        model.IsActive = enabled;
        _settings.RebuildChatModels();
        _settings.Save();
        return MapProvider(provider);
    }

    private object? LlmSetDefault(string key)
    {
        if (_settings.ChatModels.All(model => model.Key != key))
            throw new BridgeRpcException("not_found", "模型不存在或未启用");
        _settings.ChatModelKey = key;
        _settings.Save();
        return null;
    }

    private static ProviderDto MapProvider(ChatProviderConfig provider) => new(
        provider.Name,
        provider.BaseUrl,
        provider.ApiKey,
        [.. provider.Models.Select(model => new ChatProviderModelDto(
            model.ModelId,
            string.IsNullOrWhiteSpace(model.Name) ? null : model.Name,
            model.IsActive))]);

    // ===== MCP =====

    private object McpList()
    {
        var servers = _mcpConfig.Load();
        return new
        {
            servers = servers.Select(MapMcp).ToList(),
            path = _mcpConfig.DisplayPath,
            error = _mcpConfig.LastError,
        };
    }

    private async Task<object> McpSaveAsync(McpServerDto input, CancellationToken cancellationToken)
    {
        var config = ToMcpConfig(input);
        ValidateMcp(config);

        var servers = _mcpConfig.Load().ToList();
        var previousName = string.IsNullOrWhiteSpace(input.PreviousName) ? config.Name : input.PreviousName;
        var index = servers.FindIndex(server =>
            string.Equals(server.Name, previousName, StringComparison.OrdinalIgnoreCase));
        if (servers.Any(server => server != (index >= 0 ? servers[index] : null)
                && string.Equals(server.Name.Trim(), config.Name, StringComparison.OrdinalIgnoreCase)))
            throw new BridgeRpcException("conflict", "已存在同名服务器");

        if (index >= 0) servers[index] = config;
        else servers.Add(config);

        try
        {
            _mcpConfig.Save(servers);
        }
        catch (Exception ex)
        {
            throw new BridgeRpcException("io_error", $"保存 MCP 配置失败：{ex.Message}");
        }

        await _mcpTools.ResetAsync().ConfigureAwait(false);
        return new { servers = servers.Select(MapMcp).ToList() };
    }

    private async Task<object> McpDeleteAsync(string name, CancellationToken cancellationToken)
    {
        var servers = _mcpConfig.Load().ToList();
        if (servers.RemoveAll(server => string.Equals(server.Name, name, StringComparison.OrdinalIgnoreCase)) == 0)
            throw new BridgeRpcException("not_found", "MCP 服务器不存在");

        try
        {
            _mcpConfig.Save(servers);
        }
        catch (Exception ex)
        {
            throw new BridgeRpcException("io_error", $"保存 MCP 配置失败：{ex.Message}");
        }

        await _mcpTools.ResetAsync().ConfigureAwait(false);
        return new { servers = servers.Select(MapMcp).ToList() };
    }

    private async Task<object> McpTestAsync(McpServerDto input, CancellationToken cancellationToken)
    {
        var tools = await ProbeMcpAsync(ToMcpConfig(input), cancellationToken).ConfigureAwait(false);
        return new { tools = tools.Select(tool => new McpToolDto(tool.Name, tool.Description)).ToList() };
    }

    private async Task<object> McpRefreshAsync(string name, CancellationToken cancellationToken)
    {
        var server = _mcpConfig.Load().FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new BridgeRpcException("not_found", "MCP 服务器不存在");
        var tools = await ProbeMcpAsync(server, cancellationToken).ConfigureAwait(false);
        return new { tools = tools.Select(tool => new McpToolDto(tool.Name, tool.Description)).ToList() };
    }

    private async Task<IReadOnlyList<McpToolInfo>> ProbeMcpAsync(McpServerConfig server, CancellationToken cancellationToken)
    {
        try
        {
            return await _mcpTools.GetServerToolsAsync(server, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new BridgeRpcException("timeout", "连接超时或已取消");
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "MCP probe {Server} failed", server.Name);
            throw new BridgeRpcException("timeout", $"连接失败：{ex.Message}");
        }
    }

    private static McpServerConfig ToMcpConfig(McpServerDto dto) => new()
    {
        Name = (dto.Name ?? string.Empty).Trim(),
        Enabled = dto.Enabled,
        Transport = string.Equals(dto.Transport, "http", StringComparison.OrdinalIgnoreCase)
            ? McpTransportType.Http
            : McpTransportType.Stdio,
        Command = (dto.Command ?? string.Empty).Trim(),
        Arguments = [.. dto.Arguments ?? []],
        WorkingDirectory = (dto.WorkingDirectory ?? string.Empty).Trim(),
        Environment = new Dictionary<string, string>(dto.Environment ?? new Dictionary<string, string>()),
        Url = (dto.Url ?? string.Empty).Trim(),
        Headers = new Dictionary<string, string>(dto.Headers ?? new Dictionary<string, string>()),
        TimeoutSeconds = dto.TimeoutSeconds,
    };

    private static void ValidateMcp(McpServerConfig server)
    {
        if (server.Name.Length == 0) throw new BridgeRpcException("invalid_argument", "请填写服务器名称");
        if (server.TimeoutSeconds is < 1 or > 600)
            throw new BridgeRpcException("invalid_argument", "超时时间需为 1-600 之间的整数");
        if (server.Transport == McpTransportType.Stdio && server.Command.Length == 0)
            throw new BridgeRpcException("invalid_argument", "Stdio 服务器需要填写启动命令");
        if (server.Transport == McpTransportType.Http && !IsHttpUrl(server.Url))
            throw new BridgeRpcException("invalid_argument", "Http 服务器需要填写合法的 http/https 地址");
    }

    private static McpServerDto MapMcp(McpServerConfig server) => new(
        server.Name,
        server.Enabled,
        server.Transport == McpTransportType.Http ? "http" : "stdio",
        server.Command,
        [.. server.Arguments],
        server.WorkingDirectory,
        new Dictionary<string, string>(server.Environment),
        server.Url,
        new Dictionary<string, string>(server.Headers),
        server.TimeoutSeconds);

    // ===== 技能 =====

    private object SkillsList() => new
    {
        skills = _skills.Discover().Select(MapSkill).ToList(),
        directories = _skills.SearchDirectories.ToList(),
    };

    private SkillDto SkillsSetEnabled(JsonElement parameters)
    {
        var name = RequireString(parameters, "name");
        var enabled = GetBool(parameters, "enabled");
        var skill = _skills.Discover().FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new BridgeRpcException("not_found", "技能不存在");

        var disabled = _settings.DisabledSkills
            .Where(item => !string.Equals(item, skill.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!enabled) disabled.Add(skill.Name);
        _settings.DisabledSkills = disabled;
        _settings.Save();
        return MapSkill(skill);
    }

    private object? SkillsOpenFolder(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new BridgeRpcException("not_found", "目录不存在");
        try
        {
            Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new BridgeRpcException("io_error", $"打开目录失败：{ex.Message}");
        }

        return null;
    }

    private SkillDto SkillsDetail(string name)
    {
        var skill = _skills.Discover().FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new BridgeRpcException("not_found", "技能不存在");
        return MapSkill(skill) with { Content = _skills.ReadContent(skill) };
    }

    private SkillDto MapSkill(SkillInfo skill) => new(
        skill.Name,
        skill.Description,
        skill.Source,
        skill.FilePath,
        _skills.IsEnabled(skill));

    // ===== 插件 =====

    private object PluginsList() => new
    {
        plugins = _plugins.Plugins.Select(MapPlugin).ToList(),
        installDir = _plugins.InstallDirectory,
    };

    private async Task<PluginDto> PluginsSetEnabledAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var id = RequireString(parameters, "id");
        try
        {
            await _plugins.SetEnabledAsync(id, GetBool(parameters, "enabled"), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new BridgeRpcException("io_error", $"切换插件状态失败：{ex.Message}");
        }

        var plugin = _plugins.Plugins.FirstOrDefault(item =>
            string.Equals(item.Manifest.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new BridgeRpcException("not_found", "插件不存在");
        return MapPlugin(plugin);
    }

    private object PluginsConfigSchema(string id)
    {
        var plugin = FindPlugin(id);
        var schema = plugin.Manifest.ConfigSchema ?? [];
        var config = plugin.Manifest.Config;

        var fields = schema.Select(field => new ConfigFieldDto(
            field.Key,
            field.DisplayLabel,
            field.Description,
            field.Type.ToLowerInvariant(),
            field.Options,
            ReadConfigValue(config, field.Key))).ToList();

        return new { fields, config = ConfigToObject(config) };
    }

    private async Task<PluginDto> PluginsSaveConfigAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var id = RequireString(parameters, "id");
        if (!parameters.TryGetProperty("config", out var config))
            throw new BridgeRpcException("invalid_argument", "缺少配置内容");

        try
        {
            await _plugins.SaveConfigAsync(id, config.Clone(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new BridgeRpcException("io_error", $"保存插件配置失败：{ex.Message}");
        }

        return MapPlugin(FindPlugin(id));
    }

    private async Task<object> PluginsImportAsync(CancellationToken cancellationToken)
    {
        var archive = await _window.PickFileAsync("导入插件压缩包", [".zip"]).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (archive is null) return new { success = false, message = "已取消导入" };

        try
        {
            var result = await Task.Run(() => _plugins.Install(archive), cancellationToken).ConfigureAwait(false);
            if (result.Success) _plugins.Discover();
            return new { success = result.Success, message = result.Message, pluginId = result.PluginId };
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "import plugin {Archive} failed", archive);
            return new { success = false, message = $"导入失败：{ex.Message}" };
        }
    }

    private PluginDescriptor FindPlugin(string id) =>
        _plugins.Plugins.FirstOrDefault(item =>
            string.Equals(item.Manifest.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new BridgeRpcException("not_found", "插件不存在");

    private static PluginDto MapPlugin(PluginDescriptor plugin) => new(
        plugin.Manifest.Id,
        plugin.Manifest.DisplayName,
        plugin.Manifest.Version,
        plugin.Manifest.Author,
        plugin.Manifest.Description,
        plugin.IsBuiltIn,
        plugin.Directory,
        plugin.State.ToString().ToLowerInvariant(),
        plugin.Error,
        [.. plugin.ToolNames],
        plugin.FailureCount,
        plugin.Manifest.ConfigSchema is { Count: > 0 });

    private static object? ReadConfigValue(JsonElement? config, string key)
    {
        if (config is not { ValueKind: JsonValueKind.Object } root) return null;
        return root.TryGetProperty(key, out var value) ? JsonValueToObject(value) : null;
    }

    private static object? ConfigToObject(JsonElement? config)
    {
        if (config is not { ValueKind: JsonValueKind.Object } root) return new { };
        return JsonValueToObject(root);
    }

    /// <summary>把 JsonElement 转成前端可直接使用的 CLR 值（数组/对象/数字/布尔/字符串）。</summary>
    private static object? JsonValueToObject(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var integer) ? integer : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Array => value.EnumerateArray().Select(JsonValueToObject).ToList(),
        JsonValueKind.Object => value.EnumerateObject()
            .ToDictionary(property => property.Name, property => JsonValueToObject(property.Value)),
        _ => null,
    };

    // ===== 定时任务 =====

    private object TasksList()
    {
        var tasks = _scheduler.ListScheduledTasks();
        return new
        {
            tasks = tasks.Select(MapTask).ToList(),
            logs = _scheduler.Logs.Select(log => new ScheduledTaskLogDto(log.Text, log.IsError)).ToList(),
        };
    }

    private TaskDto TasksSave(TaskInputDto input)
    {
        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length == 0) throw new BridgeRpcException("invalid_argument", "请填写任务名称");
        if ((input.Content ?? string.Empty).Trim().Length == 0)
            throw new BridgeRpcException("invalid_argument", "请填写任务内容");

        var schedule = ParseSchedule(input);
        ScheduledTask? task;
        if (string.IsNullOrWhiteSpace(input.Id))
        {
            task = _scheduler.CreateScheduledTask(name, input.Content!.Trim(), schedule);
            task = _scheduler.UpdateScheduledTask(task.Id, null, null, null, input.Enabled) ?? task;
        }
        else
        {
            task = _scheduler.UpdateScheduledTask(input.Id, name, input.Content!.Trim(), schedule, input.Enabled)
                   ?? throw new BridgeRpcException("not_found", "任务不存在");
        }

        _scheduler.Refresh();
        return MapTask(task);
    }

    private object TasksDelete(string id)
    {
        if (_scheduler.DeleteScheduledTask(id) is null)
            throw new BridgeRpcException("not_found", "任务不存在");
        return new { tasks = _scheduler.ListScheduledTasks().Select(MapTask).ToList() };
    }

    private TaskDto TasksSetEnabled(JsonElement parameters)
    {
        var id = RequireString(parameters, "id");
        var enabled = GetBool(parameters, "enabled");
        var task = _scheduler.UpdateScheduledTask(id, null, null, null, enabled)
                   ?? throw new BridgeRpcException("not_found", "任务不存在");
        _scheduler.Refresh();
        return MapTask(task);
    }

    private object? TasksRunNow(string id)
    {
        var task = _scheduler.FindTask(id) ?? throw new BridgeRpcException("not_found", "任务不存在");
        _scheduler.RunNow(task);
        return null;
    }

    private object TasksNextRun(string id)
    {
        var task = _scheduler.FindTask(id) ?? throw new BridgeRpcException("not_found", "任务不存在");
        _scheduler.Refresh();
        return new { nextRunAt = ToIso(task.NextRunAt) };
    }

    private object? TasksClearLogs()
    {
        _scheduler.ClearLogs();
        return null;
    }

    private static ScheduledTaskSchedule ParseSchedule(TaskInputDto input)
    {
        var kind = (input.Kind ?? "daily").Trim().ToLowerInvariant() switch
        {
            "once" => ScheduledTaskKind.Once,
            "weekly" => ScheduledTaskKind.Weekly,
            "interval" => ScheduledTaskKind.Interval,
            _ => ScheduledTaskKind.Daily,
        };

        TimeSpan? time = null;
        if (TryParseTime(input.TimeOfDay ?? input.OnceTime) is { } parsedTime) time = parsedTime;

        DateTimeOffset? onceDate = null;
        if (kind == ScheduledTaskKind.Once)
        {
            if (!DateTime.TryParse(input.OnceDate, out var date))
                throw new BridgeRpcException("invalid_argument", "单次任务需要合法的日期");
            var timeOfDay = time ?? new TimeSpan(9, 0, 0);
            onceDate = new DateTimeOffset(date.Date.Add(timeOfDay), TimeZoneInfo.Local.GetUtcOffset(date.Date));
            time = timeOfDay;
        }

        IReadOnlyList<DayOfWeek>? weekdays = null;
        if (kind == ScheduledTaskKind.Weekly)
            weekdays = [.. (input.Weekdays ?? [])
                .Where(day => day is >= 0 and <= 6)
                .Select(day => (DayOfWeek)day)
                .Distinct()];

        return new ScheduledTaskSchedule(
            kind,
            time,
            onceDate,
            weekdays,
            kind == ScheduledTaskKind.Interval ? Math.Clamp(input.IntervalMinutes, 1, 43200) : null);
    }

    private static TimeSpan? TryParseTime(string? text) =>
        TimeSpan.TryParse(text, out var time) ? time : null;

    private static TaskDto MapTask(ScheduledTask task) => new(
        task.Id,
        task.Name,
        task.Content,
        task.Enabled,
        task.Kind.ToString().ToLowerInvariant(),
        task.OnceDate?.ToString("yyyy-MM-dd"),
        task.OnceTime?.ToString(@"hh\:mm"),
        task.TimeOfDay?.ToString(@"hh\:mm"),
        [.. task.Weekdays.Select(day => (int)day)],
        task.IntervalMinutes,
        task.Completed,
        ToIso(task.NextRunAt),
        ToIso(task.LastRunAt));

    private static string? ToIso(DateTime? value) =>
        value is { } time ? new DateTimeOffset(time).ToString("O") : null;

    // ===== Live2D 模型 =====

    private object ModelsList() => new
    {
        models = _settings.Live2DModels.Select((model, index) => MapModel(model, index)).ToList(),
        activeIndex = _settings.ActiveLive2DModelIndex,
    };

    private async Task<object> ModelsImportAsync(bool folder, CancellationToken cancellationToken)
    {
        string? source;
        string? archive = null;
        try
        {
            if (folder)
            {
                source = await _window.PickFolderAsync("选择 Live2D 模型目录").ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (source is null) return CancelImport();
                archive = ZipDirectory(source);
            }
            else
            {
                source = await _window.PickFileAsync("选择 Live2D 模型压缩包", [".zip"]).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (source is null) return CancelImport();
                archive = source;
            }

            var result = await Task.Run(() => _models.Import(archive), cancellationToken).ConfigureAwait(false);
            if (result.Success && result.Model is { } imported)
            {
                _ui.Invoke(() =>
                {
                    var existing = _settings.Live2DModels.FirstOrDefault(model =>
                        string.Equals(model.Name, imported.Name, StringComparison.OrdinalIgnoreCase));
                    if (existing is null)
                    {
                        _settings.Live2DModels.Add(imported);
                        _settings.ActiveLive2DModelIndex = _settings.Live2DModels.Count - 1;
                    }
                    else
                    {
                        _settings.ActiveLive2DModelIndex = _settings.Live2DModels.IndexOf(existing);
                    }

                    _settings.NotifyActiveLive2DModelIndexChanged();
                    _settings.Save();
                });
            }

            List<Live2DModelDto> models = [];
            _ui.Invoke(() => models = ModelsListModels());
            return new { models, message = result.Message };
        }
        finally
        {
            if (folder && archive is not null) TryDelete(archive);
        }
    }

    private object CancelImport()
    {
        List<Live2DModelDto> models = [];
        _ui.Invoke(() => models = ModelsListModels());
        return new { models, message = "已取消导入" };
    }

    private object ModelsRemove(int index)
    {
        var model = ModelAt(index);
        var result = _models.Remove(model);
        if (!result.Success) throw new BridgeRpcException("io_error", result.Message);

        var wasActive = index == _settings.ActiveLive2DModelIndex;
        _settings.Live2DModels.RemoveAt(index);
        if (_settings.Live2DModels.Count == 0)
        {
            _settings.ActiveLive2DModelIndex = 0;
        }
        else if (wasActive)
        {
            _settings.ActiveLive2DModelIndex = Math.Clamp(index, 0, _settings.Live2DModels.Count - 1);
        }
        else if (_settings.ActiveLive2DModelIndex > index)
        {
            _settings.ActiveLive2DModelIndex -= 1;
        }

        _settings.NotifyActiveLive2DModelIndexChanged();
        _settings.Save();
        return new { models = ModelsListModels(), activeIndex = _settings.ActiveLive2DModelIndex };
    }

    private object? ModelsSetActive(int index)
    {
        _ = ModelAt(index);
        if (_settings.ActiveLive2DModelIndex == index)
            _settings.NotifyActiveLive2DModelIndexChanged();
        else
            _settings.ActiveLive2DModelIndex = index;
        _settings.Save();
        return null;
    }

    private object ModelsValidate(int index)
    {
        var model = ModelAt(index);
        var result = _models.Update(model);
        return new { ok = result.Success, message = result.Message };
    }

    private Live2DModelConfig ModelAt(int index)
    {
        if (index < 0 || index >= _settings.Live2DModels.Count)
            throw new BridgeRpcException("not_found", "模型不存在");
        return _settings.Live2DModels[index];
    }

    private List<Live2DModelDto> ModelsListModels() =>
        [.. _settings.Live2DModels.Select((model, index) => MapModel(model, index))];

    private Live2DModelDto MapModel(Live2DModelConfig model, int index)
    {
        var result = _models.Update(model);
        return new Live2DModelDto(
            model.Name,
            model.FolderName,
            index == _settings.ActiveLive2DModelIndex,
            model.GameExpression,
            model.MusicExpression,
            result.Success,
            result.Success ? null : result.Message);
    }

    private static string ZipDirectory(string directory)
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"soufflepet-model-{Guid.NewGuid():N}.zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var root = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            zip.CreateEntryFromFile(file, $"{root}/{relative}", CompressionLevel.Fastest);
        }

        return zipPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // 临时文件清理失败不影响导入结果
        }
    }

    // ===== 外观 =====

    private object? SetSidebarCollapsed(bool collapsed)
    {
        _settings.UiSidebarCollapsed = collapsed;
        _settings.Save();
        return null;
    }

    // ===== 参数与通用工具 =====

    private static T Read<T>(JsonElement parameters) where T : class
    {
        if (parameters.ValueKind != JsonValueKind.Object)
            throw new BridgeRpcException("invalid_argument", "参数必须是对象");
        try
        {
            return JsonSerializer.Deserialize<T>(parameters.GetRawText(), WebJson.Options)
                   ?? throw new BridgeRpcException("invalid_argument", "参数不能为空");
        }
        catch (JsonException ex)
        {
            throw new BridgeRpcException("invalid_argument", $"参数格式错误：{ex.Message}");
        }
    }

    private static string? GetString(JsonElement parameters, string name) =>
        parameters.ValueKind == JsonValueKind.Object
        && parameters.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string RequireString(JsonElement parameters, string name) =>
        GetString(parameters, name) ?? throw new BridgeRpcException("invalid_argument", $"缺少参数 {name}");

    private static bool GetBool(JsonElement parameters, string name) =>
        parameters.ValueKind == JsonValueKind.Object
        && parameters.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static int GetInt(JsonElement parameters, string name) =>
        parameters.ValueKind == JsonValueKind.Object
        && parameters.TryGetProperty(name, out var value)
        && value.TryGetInt32(out var number)
            ? number
            : throw new BridgeRpcException("invalid_argument", $"缺少参数 {name}");

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}