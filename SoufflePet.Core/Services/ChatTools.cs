// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Serilog;
using SoufflePet.Core.Chat;
using SoufflePet.Models;
using SoufflePet.Services.Plugins;

namespace SoufflePet.Services;

/// <summary>
/// 对话模型可调用的本地工具集（含切换表情、执行命令这两个有副作用的工具）：
/// 由 AIFunctionFactory 依据方法签名与 <see cref="DescriptionAttribute"/> 生成工具声明，
/// 模型按需发起调用，本地执行后再把结果回传给模型。
/// </summary>
public sealed class ChatTools
{
    private static readonly ILogger Logger = Log.ForContext<ChatTools>();

    /// <summary>命令执行的默认超时（秒）：模型没指定 timeout_seconds 时使用。</summary>
    private const int DefaultShellTimeoutSeconds = 30;

    /// <summary>命令执行允许的最大超时（秒）：模型传得更大时按此上限处理。</summary>
    private const int MaxShellTimeoutSeconds = 1800;

    /// <summary>命令输出回传给模型的最大长度，超出截断。</summary>
    private const int ShellOutputMaxLength = 4000;

    /// <summary>
    /// 主进程退出后等待管道 EOF 的宽限时间：命令拉起后台进程（如浏览器 daemon）时，
    /// 管道写端仍被其持有，EOF 永远不会到来，只能收完已缓冲的输出就返回。
    /// </summary>
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>媒体文件（图片/音频）读入内存的最大字节数，超出只回报文件信息、不读内容。</summary>
    private const long ReadFileMaxBytes = 20L * 1024 * 1024;

    /// <summary>文本文件回传给模型的最大字符数，超出截断。</summary>
    private const int ReadFileMaxChars = 20_000;

    /// <summary>技能正文回传给模型的最大长度，超出截断。</summary>
    private const int SkillContentMaxLength = 8000;

    /// <summary>本地展示文件的最大字节数，超出时只回报文件信息、不展示。</summary>
    private const long ShowFileMaxBytes = 50L * 1024 * 1024;

    /// <summary>可编辑文本文件的最大字节数：再大就不读入内存，避免一次编辑把进程内存吃满。</summary>
    private const long MaxEditableBytes = 1024 * 1024;

    /// <summary>写入内容的字符上限。</summary>
    private const int MaxEditChars = 512 * 1024;

    /// <summary>写文件统一用不带 BOM 的 UTF-8。</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>按扩展名识别图片，决定平台按图片发送还是在窗口里预览。</summary>
    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

    /// <summary>一次提问最多展示的问题数，超出的忽略（面板放不下，也避免模型刷屏）。</summary>
    private const int AskUserMaxQuestions = 6;

    /// <summary>每个问题的预设选项上限，超出的忽略。</summary>
    private const int AskUserMaxOptions = 6;

    /// <summary>联网搜索接口（百度千帆 AI 搜索）：POST + Bearer API Key。</summary>
    private const string WebSearchEndpoint = "https://qianfan.baidubce.com/v2/ai_search/web_search";

    /// <summary>联网搜索使用的命名 HttpClient（在组合根注册，超时 20 秒）。</summary>
    public const string WebSearchClientName = "web-search";

    /// <summary>单条搜索摘要回传给模型的最大长度，超出截断。</summary>
    private const int SearchSnippetMaxLength = 400;

    /// <summary>搜索结果整体回传给模型的最大长度，超出截断。</summary>
    private const int SearchOutputMaxLength = 6000;

    /// <summary>定时任务内容在列表与审批面板里的预览长度，超出截断。</summary>
    private const int TaskContentPreviewLength = 80;

    /// <summary>定时任务允许的最小间隔（分钟）。</summary>
    private const int MinTaskIntervalMinutes = 1;

    /// <summary>定时任务允许的最大间隔（分钟，7 天）。</summary>
    private const int MaxTaskIntervalMinutes = 10080;

    private readonly SouffleSettings _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPetController _expressions;
    private readonly ChatToolAccess _access;
    private readonly ChatMemory _memory;
    private readonly IMcpToolService _mcp;
    private readonly ISkillService _skills;
    private readonly IPluginService _plugins;
    private readonly UserQuestionService _questions;
    private readonly PlatformService _platforms;
    private readonly ChatFileService _files;
    private readonly IScheduledTaskService _scheduledTasks;
    private readonly IStatisticsService _statistics;

    public ChatTools(
        SouffleSettings settings,
        IHttpClientFactory httpClientFactory,
        IPetController expressions,
        ChatToolAccess access,
        ChatMemory memory,
        IMcpToolService mcp,
        ISkillService skills,
        IPluginService plugins,
        UserQuestionService questions,
        PlatformService platforms,
        ChatFileService files,
        IScheduledTaskService scheduledTasks,
        IStatisticsService statistics)
    {
        _settings = settings;
        _httpClientFactory = httpClientFactory;
        _expressions = expressions;
        _access = access;
        _memory = memory;
        _mcp = mcp;
        _skills = skills;
        _plugins = plugins;
        _questions = questions;
        _platforms = platforms;
        _files = files;
        _scheduledTasks = scheduledTasks;
        _statistics = statistics;
        Tools =
        [
            AIFunctionFactory.Create(
                WebSearch,
                "web_search",
                "联网搜索实时信息"),
            AIFunctionFactory.Create(
                GetExpressions,
                "get_expressions",
                "列出当前 Live2D 模型可用的表情名称"),
            AIFunctionFactory.Create(
                GetCurrentExpression,
                "get_current_expression",
                "获取当前显示的 Live2D 表情"),
            AIFunctionFactory.Create(
                SetExpression,
                "set_expression",
                "切换桌宠的 Live2D 表情；空字符串表示恢复默认"),
            AIFunctionFactory.Create(
                ReadFile,
                "read_file",
                "读取文件内容，支持文本、图片、音频"),
            AIFunctionFactory.Create(
                RunShellCommand,
                "run_shell_command",
                "执行 PowerShell 命令，返回退出码与输出"),
            AIFunctionFactory.Create(
                AskUser,
                "ask_user",
                "向用户提问以补全信息：每个问题给 2-5 个预设选项，"
                + "界面会附带一个「其他」选项；用户全部回答后返回结果"),
            AIFunctionFactory.Create(
                Remember,
                "remember",
                "写入长期记忆；同一个 key 会覆盖旧值"),
            AIFunctionFactory.Create(
                Forget,
                "forget",
                "删除长期记忆里的一条记录"),
            AIFunctionFactory.Create(
                Recall,
                "recall",
                "按关键词检索过去的对话记录"),
            AIFunctionFactory.Create(
                ReadSkill,
                "read_skill",
                "读取某个技能的完整说明"),
            AIFunctionFactory.Create(
                ListPlugins,
                "list_plugins",
                "列出已发现插件的 id、版本、启用状态与提供的工具"),
            AIFunctionFactory.Create(
                SetPluginEnabled,
                "set_plugin_enabled",
                "启用或停用某个插件：启用后其工具从下一轮对话开始可用"),
            AIFunctionFactory.Create(
                ReloadPlugins,
                "reload_plugins",
                "重新扫描插件目录，发现新增或移除的插件；已加载插件保持运行状态"),
            AIFunctionFactory.Create(
                GetMessages,
                "get_messages",
                "读取消息平台某个会话的最近消息"),
            AIFunctionFactory.Create(
                SendMessage,
                "send_message",
                "向消息平台的某个会话发送消息"),
            AIFunctionFactory.Create(
                SendAttachment,
                "send_attachment",
                "向消息平台的某个会话发送文件"),
            AIFunctionFactory.Create(
                ShowFile,
                "show_file",
                "向用户展示文件"),
            AIFunctionFactory.Create(
                WriteFile,
                "write_file",
                "写入文本文件：新建或整体覆盖；相对路径按当前会话工作区解析"),
            AIFunctionFactory.Create(
                EditFile,
                "edit_file",
                "编辑文本文件：把一段旧文本替换成新文本；返回改动摘要"),
            AIFunctionFactory.Create(
                ListScheduledTasks,
                "list_scheduled_tasks",
                "列出当前的所有定时任务"),
            AIFunctionFactory.Create(
                CreateScheduledTask,
                "create_scheduled_task",
                "新建一条定时任务"),
            AIFunctionFactory.Create(
                UpdateScheduledTask,
                "update_scheduled_task",
                "修改一条定时任务"),
            AIFunctionFactory.Create(
                DeleteScheduledTask,
                "delete_scheduled_task",
                "删除一条定时任务"),
            AIFunctionFactory.Create(
                GetStatistics,
                "get_statistics",
                "查询统计信息")
        ];
        LocalToolNames = [.. Tools.Select(t => t.Name)];
    }

    /// <summary>可传给 ChatOptions.Tools 的工具列表。</summary>
    private IReadOnlyList<AITool> Tools { get; }

    /// <summary>本地工具名：插件工具与之重名时由插件服务自动加前缀。</summary>
    private IReadOnlyCollection<string> LocalToolNames { get; }

    /// <summary>
    /// 本次请求可用的全部工具：本地工具 + 已启用插件的工具 + 已连接 MCP 服务器的工具（首次调用时连接）。
    /// 插件或 MCP 失败不影响其余工具。
    /// </summary>
    public async Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken)
    {
        var pluginTools = await _plugins.GetToolsAsync(LocalToolNames, cancellationToken).ConfigureAwait(false);
        var mcpTools = await _mcp.GetToolsAsync(cancellationToken).ConfigureAwait(false);

        var tools = new List<AITool>(Tools.Count + pluginTools.Count + mcpTools.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in Tools.Concat(pluginTools).Concat(mcpTools))
        {
            if (seen.Add(tool.Name)) tools.Add(tool);
        }

        // tools 数组同样属于请求前缀的一部分：按名称固定排序，避免插件 / MCP 服务器的
        // 枚举顺序变化导致每轮 tools 段不同、服务端前缀缓存整段失效。
        tools.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
        return tools;
    }

    /// <summary>统计查询：不传 item 返回全部统计项，传名称/标识返回匹配的单项。</summary>
    private string GetStatistics(string? item = null)
    {
        var items = _statistics.Snapshot();
        if (string.IsNullOrWhiteSpace(item))
            return "桌宠统计信息：\n" + string.Join("\n", items.Select(x => $"- {x.Label}：{x.Value}"));

        var hit = items.FirstOrDefault(x =>
            x.Key.Contains(item, StringComparison.OrdinalIgnoreCase) ||
            x.Label.Contains(item, StringComparison.OrdinalIgnoreCase));
        return hit is null
            ? $"没有名为「{item}」的统计项，可查询：{string.Join("、", items.Select(x => x.Label))}。"
            : $"{hit.Label}：{hit.Value}";
    }

    /// <summary>
    /// 联网搜索：把关键词交给百度千帆 AI 搜索，汇总返回网页的标题、链接与摘要。
    /// 摘要与总量都做截断，避免一次调用把上下文撑爆。
    /// </summary>
    private async Task<string> WebSearch(
        [Description("搜索关键词；百度侧只取前 72 个字符")]
        string query,
        [Description("时间范围：week、month、semiyear、year；不传表示不限")]
        string? recency = null,
        CancellationToken cancellationToken = default)
    {
        var apiKey = _settings.WebSearchApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return "联网搜索未配置：请在 SoufflePet.json 中填写 WebSearchApiKey（百度千帆 API Key）后重启应用。";

        query = query.Trim();
        if (query.Length == 0) return "搜索关键词不能为空。";

        var payload = new Dictionary<string, object?>
        {
            ["messages"] = new[] { new { role = "user", content = query } },
            ["search_source"] = "baidu_search_v2",
            ["edition"] = "standard",
            ["resource_type_filter"] = new[] { new { type = "web", top_k = 10 } },
        };
        if (!string.IsNullOrWhiteSpace(recency)) payload["search_recency_filter"] = recency.Trim();

        using var request = new HttpRequestMessage(HttpMethod.Post, WebSearchEndpoint);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");

        using var response = await _httpClientFactory.CreateClient(WebSearchClientName)
            .SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return $"联网搜索失败（HTTP {(int)response.StatusCode}）：{Truncate(body, 300)}";

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.TryGetProperty("code", out var code) && code.ValueKind != JsonValueKind.Null)
            return $"联网搜索失败（{code.GetRawText()}）：{ReadString(root, "message")}".Trim();

        if (!root.TryGetProperty("references", out var references) || references.ValueKind != JsonValueKind.Array)
            return "联网搜索没有返回结果。";

        var builder = new StringBuilder("联网搜索结果：");
        var count = 0;
        foreach (var item in references.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            // 本次只请求网页，视频/图片等结果直接跳过
            if (ReadString(item, "type") is { } type &&
                !string.Equals(type, "web", StringComparison.OrdinalIgnoreCase)) continue;

            var url = ReadString(item, "url");
            if (string.IsNullOrWhiteSpace(url)) continue;

            var title = ReadString(item, "title") ?? ReadString(item, "web_anchor") ?? "(无标题)";
            var snippet = ReadString(item, "snippet") ?? ReadString(item, "content") ?? string.Empty;
            var date = ReadString(item, "date");
            var site = ReadString(item, "website");

            builder.Append("\n\n").Append(++count).Append(". ").Append(title);
            if (!string.IsNullOrEmpty(date)) builder.Append("（").Append(date).Append("）");
            builder.Append('\n').Append(url);
            if (!string.IsNullOrWhiteSpace(site)) builder.Append("\n来源：").Append(site);
            if (snippet.Length > 0) builder.Append('\n').Append(Truncate(snippet.Trim(), SearchSnippetMaxLength));

            if (builder.Length >= SearchOutputMaxLength)
            {
                builder.Append("\n…（结果过多，已截断）");
                break;
            }
        }

        return count == 0 ? "联网搜索没有返回网页结果。" : builder.ToString();
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private string GetExpressions()
    {
        var names = _expressions.GetExpressionNames();
        var model = _settings.ActiveLive2DModel?.Name ?? "当前模型";
        return names.Count == 0
            ? $"当前模型 {model} 没有配置表情。"
            : $"当前模型 {model} 的可用表情：{string.Join("、", names)}。";
    }

    private string GetCurrentExpression() =>
        _expressions.GetCurrentExpression() is { Length: > 0 } name
            ? $"当前表情：「{name}」。"
            : "当前没有显示表情（默认状态）。";

    private Task<string> SetExpression(
        [Description("表情名称（取自 get_expressions），空字符串表示恢复默认")]
        string? name)
        => _expressions.SetExpressionAsync(name);

    private string Remember(
        [Description("记忆的键，简短词，如「称呼」")]
        string key,
        [Description("记忆的内容，一句话")] string value)
        => _memory.Remember(key, value);

    private string Forget(
        [Description("要删除的记忆键")] string key)
        => _memory.Forget(key);

    private string Recall(
        [Description("检索关键词")] string query)
        => _memory.Recall(query);

    private string ReadSkill(
        [Description("技能名称")] string name)
    {
        if (_skills.DiscoverEnabled().FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } skill)
            return $"没有名为「{name}」的可用技能，请从系统提示词的技能清单中选择。";

        var content = _skills.ReadContent(skill);
        if (content.Length == 0) return $"技能「{skill.Name}」没有正文内容。";

        var builder = new StringBuilder($"技能「{skill.Name}」（{skill.Source}）：\n");
        return content.Length <= SkillContentMaxLength
            ? builder.Append(content).ToString()
            : builder.Append(content[..SkillContentMaxLength]).Append("\n…（已截断）").ToString();
    }

    /// <summary>列出全部已发现插件（数据取自插件服务，这里只负责转成可读文本）。</summary>
    private string ListPlugins()
    {
        var plugins = _plugins.Plugins;
        if (plugins.Count == 0) return "当前没有发现任何插件。";

        var builder = new StringBuilder($"共 {plugins.Count} 个插件：");
        foreach (var plugin in plugins)
        {
            var manifest = plugin.Manifest;
            builder.Append("\n- id=").Append(manifest.Id)
                .Append(" 名称「").Append(manifest.DisplayName).Append('」')
                .Append(" 版本=").Append(manifest.Version)
                .Append(" 来源=").Append(plugin.IsBuiltIn ? "内置" : "用户")
                .Append(" 启用=").Append(plugin.Enabled ? "是" : "否")
                .Append(" 运行=").Append(StateText(plugin.State));
            if (plugin.ToolNames.Count > 0)
                builder.Append(" 工具=").Append(string.Join('、', plugin.ToolNames));
            if (plugin.FailureCount > 0)
                builder.Append(" 失败次数=").Append(plugin.FailureCount);
            if (!string.IsNullOrWhiteSpace(plugin.Error))
                builder.Append(" 错误=").Append(plugin.Error);
        }
        return builder.ToString();
    }

    /// <summary>启用或停用插件：插件服务负责加载/卸载，这里校验 id 并转述结果。</summary>
    private async Task<string> SetPluginEnabled(
        [Description("插件 id，取自 list_plugins")] string pluginId,
        [Description("启用/停用")] bool enabled,
        CancellationToken cancellationToken = default)
    {
        var id = pluginId.Trim();
        if (id.Length == 0) return "请提供插件 id，可先用 list_plugins 查看。";

        if (_plugins.Plugins.FirstOrDefault(p => string.Equals(p.Manifest.Id, id, StringComparison.OrdinalIgnoreCase))
            is not { } plugin)
            return $"没有找到插件「{id}」，请先用 list_plugins 确认 id。";

        await _plugins.SetEnabledAsync(plugin.Manifest.Id, enabled, cancellationToken).ConfigureAwait(false);

        var updated = _plugins.Plugins.FirstOrDefault(
            p => string.Equals(p.Manifest.Id, plugin.Manifest.Id, StringComparison.OrdinalIgnoreCase));
        var name = updated?.Manifest.DisplayName ?? plugin.Manifest.DisplayName;

        if (enabled && updated?.State is PluginState.Failed)
            return $"插件「{name}」启用失败：{updated.Error ?? "未知原因"}";

        return enabled
            ? $"已启用插件「{name}」（{plugin.Manifest.Id}），其工具会从下一轮对话开始可用。"
            : $"已停用插件「{name}」（{plugin.Manifest.Id}），其工具已从下一轮对话移除。";
    }

    /// <summary>重新扫描插件目录：只发现新增/移除，不重载已加载插件。</summary>
    private string ReloadPlugins()
    {
        _plugins.Discover();
        var plugins = _plugins.Plugins;
        return $"已重新扫描插件目录：共发现 {plugins.Count} 个插件，其中 {plugins.Count(p => p.Enabled)} 个已启用。"
            + "已加载插件保持原状态；新增插件的工具会在下一轮对话加载。";
    }

    private static string StateText(PluginState state) => state switch
    {
        PluginState.Enabled => "已加载",
        PluginState.Loading => "加载中",
        PluginState.Failed => "加载失败",
        PluginState.Disabled => "已停用",
        _ => "未加载",
    };

    /// <summary>列出全部定时任务（数据取自调度服务，这里只负责转成可读文本）。</summary>
    private string ListScheduledTasks()
    {
        var tasks = _scheduledTasks.ListScheduledTasks();
        if (tasks.Count == 0) return "当前没有任何定时任务。";

        var builder = new StringBuilder($"共 {tasks.Count} 条定时任务：");
        foreach (var task in tasks)
        {
            builder.Append("\n- id=").Append(task.Id)
                .Append(" 名称「").Append(task.Name).Append('」')
                .Append(" 状态=").Append(task.Enabled ? "启用" : "停用")
                .Append(" 计划=").Append(task.ScheduleText)
                .Append(" 下次触发=").Append(task.NextRunText)
                .Append(" 内容=").Append(ContentPreview(task.Content));
        }
        return builder.ToString();
    }

    /// <summary>新建定时任务：校验参数后交给调度服务落盘。</summary>
    private async Task<string> CreateScheduledTask(
        [Description("任务名称，例如「喝水提醒」")] string name,
        [Description("任务内容，例如「提醒我该喝水了」")] string content,
        [Description("调度方式：once=单次、daily=每日、weekly=每周、interval=固定间隔；不传按单次处理")] string? kind = null,
        [Description("触发时刻，24 小时制 HH:mm，例如 09:30；interval 方式不需要")] string? time = null,
        [Description("once 方式的触发日期 yyyy-MM-dd，例如 2026-10-27；不传表示今天")] string? date = null,
        [Description("weekly 方式选中的星期，逗号分隔，1=周一 … 7=周日，例如 \"1,3,5\"；留空表示每天")] string? weekdays = null,
        [Description("interval 方式的间隔分钟数，1-10080；不传默认 60")] int? intervalMinutes = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "任务内容不能为空";
        if (!TryParseKind(kind, out var parsedKind)) return UnrecognizedKind(kind);
        if (!TryBuildSchedule(parsedKind, time, date, weekdays, intervalMinutes, out var schedule, out var error))
            return error;

        var taskName = string.IsNullOrWhiteSpace(name) ? "新任务" : name.Trim();
        var taskContent = content.Trim();
        var preview = ScheduledTaskService.PreviewTask(null, taskName, taskContent, schedule, enabled: null);
        var detail = $"新建任务「{preview.Name}」（{preview.ScheduleText}）\n内容：{ContentPreview(preview.Content)}";
        if (!await _access.RequestApprovalAsync("create_scheduled_task", detail, cancellationToken).ConfigureAwait(false))
            return ApprovalDenied(cancellationToken, "新建定时任务");

        var created = _scheduledTasks.CreateScheduledTask(taskName, taskContent, schedule);
        return $"已新建定时任务「{created.Name}」（{created.ScheduleText}），下次触发 {created.NextRunText}。";
    }

    /// <summary>修改定时任务：校验参数后交给调度服务落盘（只改传入的字段）。</summary>
    private async Task<string> UpdateScheduledTask(
        [Description("要修改的任务 id")] string id,
        [Description("新的任务名称；不传保持原样")] string? name = null,
        [Description("新的任务内容；不传保持原样")] string? content = null,
        [Description("新的调度方式：once/daily/weekly/interval；不传保持原样")] string? kind = null,
        [Description("新的触发时刻，HH:mm；不传保持原样")] string? time = null,
        [Description("once 方式的新触发日期 yyyy-MM-dd；不传保持原样")] string? date = null,
        [Description("weekly 方式新的星期，例如 \"1,3,5\"；传空字符串表示每天；不传保持原样")] string? weekdays = null,
        [Description("interval 方式新的间隔分钟数，1-10080；不传保持原样")] int? intervalMinutes = null,
        [Description("是否启用；不传保持原样")] bool? enabled = null,
        CancellationToken cancellationToken = default)
    {
        if (_scheduledTasks.FindTask(id) is not { } task) return TaskMissing(id);

        var targetKind = task.Kind;
        if (kind is not null && !TryParseKind(kind, out targetKind)) return UnrecognizedKind(kind);

        ScheduledTaskSchedule? schedule = null;
        if (kind is not null || time is not null || date is not null || weekdays is not null || intervalMinutes is not null)
        {
            if (!TryBuildSchedule(targetKind, time, date, weekdays, intervalMinutes, out var parsed, out var error))
                return error;
            schedule = parsed;
        }

        var newName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        var newContent = string.IsNullOrWhiteSpace(content) ? null : content.Trim();
        if (newName is null && newContent is null && schedule is null && enabled is null)
            return "没有给出任何要修改的内容：请至少提供名称、内容、调度或启用状态中的一项。";

        var preview = ScheduledTaskService.PreviewTask(task, newName, newContent, schedule, enabled);
        var detail = $"修改任务「{task.Name}」\n当前：{task.ScheduleText}，{(task.Enabled ? "启用" : "停用")}\n"
            + $"改动后：{preview.ScheduleText}，{(preview.Enabled ? "启用" : "停用")}\n内容：{ContentPreview(preview.Content)}";
        if (!await _access.RequestApprovalAsync("update_scheduled_task", detail, cancellationToken).ConfigureAwait(false))
            return ApprovalDenied(cancellationToken, "修改定时任务");

        return _scheduledTasks.UpdateScheduledTask(task.Id, newName, newContent, schedule, enabled) is { } updated
            ? $"已修改定时任务「{updated.Name}」（{updated.ScheduleText}），下次触发 {updated.NextRunText}。"
            : TaskMissing(id);
    }

    /// <summary>删除定时任务：先查后交给调度服务删除。</summary>
    private async Task<string> DeleteScheduledTask(
        [Description("要删除的任务 id")] string id,
        CancellationToken cancellationToken = default)
    {
        if (_scheduledTasks.FindTask(id) is not { } task) return TaskMissing(id);

        var detail = $"删除任务「{task.Name}」（{task.ScheduleText}）\n内容：{ContentPreview(task.Content)}";
        if (!await _access.RequestApprovalAsync("delete_scheduled_task", detail, cancellationToken).ConfigureAwait(false))
            return ApprovalDenied(cancellationToken, "删除定时任务");

        return _scheduledTasks.DeleteScheduledTask(task.Id) is { } removed
            ? $"已删除定时任务「{removed.Name}」。"
            : TaskMissing(id);
    }

    /// <summary>校验并把模型的调度参数组装成强类型对象：全部参数解析成功才返回。</summary>
    private static bool TryBuildSchedule(
        ScheduledTaskKind kind, string? time, string? date, string? weekdays, int? intervalMinutes,
        out ScheduledTaskSchedule schedule, out string error)
    {
        schedule = new ScheduledTaskSchedule(kind);
        error = string.Empty;

        TimeSpan? parsedTime = null;
        if (time is not null)
        {
            if (!TryParseTimeOfDay(time, out var timeValue))
            {
                error = $"触发时刻「{time}」无法识别，请用 24 小时制 HH:mm，例如 09:30。";
                return false;
            }
            parsedTime = timeValue;
        }

        DateTimeOffset? parsedDate = null;
        if (date is not null)
        {
            if (!TryParseDate(date, out var dateValue))
            {
                error = $"日期「{date}」无法识别，请用 yyyy-MM-dd，例如 2026-10-27。";
                return false;
            }
            parsedDate = dateValue;
        }

        List<DayOfWeek>? parsedWeekdays = null;
        if (weekdays is not null)
        {
            if (!TryParseWeekdays(weekdays, out var daysValue))
            {
                error = $"星期「{weekdays}」无法识别，请用逗号分隔的 1-7（1=周一 … 7=周日），例如 \"1,3,5\"。";
                return false;
            }
            parsedWeekdays = daysValue;
        }

        int? parsedInterval = null;
        if (intervalMinutes is { } minutes)
        {
            if (minutes is < MinTaskIntervalMinutes or > MaxTaskIntervalMinutes)
            {
                error = $"间隔分钟数 {minutes} 超出范围（{MinTaskIntervalMinutes}-{MaxTaskIntervalMinutes}）。";
                return false;
            }
            parsedInterval = minutes;
        }

        schedule = new ScheduledTaskSchedule(kind, parsedTime, parsedDate, parsedWeekdays, parsedInterval);
        return true;
    }

    /// <summary>解析调度方式（中英文别名）；未提供（null / 空）时按默认的单次任务处理。</summary>
    private static bool TryParseKind(string? kind, out ScheduledTaskKind result)
    {
        result = ScheduledTaskKind.Once;
        switch (kind?.Trim().ToLowerInvariant())
        {
            case null:
            case "":
            case "default":
            case "once":
            case "single":
            case "单次":
                result = ScheduledTaskKind.Once;
                return true;
            case "daily":
            case "day":
            case "每日":
            case "每天":
                result = ScheduledTaskKind.Daily;
                return true;
            case "weekly":
            case "week":
            case "每周":
                result = ScheduledTaskKind.Weekly;
                return true;
            case "interval":
            case "repeat":
            case "间隔":
            case "循环":
                result = ScheduledTaskKind.Interval;
                return true;
            default:
                return false;
        }
    }

    /// <summary>解析触发时刻（hh:mm / h:mm / hh:mm:ss）。</summary>
    private static bool TryParseTimeOfDay(string time, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (TimeSpan.TryParseExact(time.Trim(), [@"hh\:mm", @"h\:mm", @"hh\:mm\:ss", @"h\:mm\:ss"], CultureInfo.InvariantCulture, out var parsed)
            && parsed >= TimeSpan.Zero && parsed < TimeSpan.FromDays(1))
        {
            result = parsed;
            return true;
        }
        return false;
    }

    /// <summary>解析单次任务的日期（yyyy-MM-dd）。</summary>
    private static bool TryParseDate(string date, out DateTimeOffset result)
    {
        if (DateTimeOffset.TryParseExact(date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            result = parsed;
            return true;
        }
        result = default;
        return false;
    }

    /// <summary>解析星期（1=周一 … 7=周日，逗号/顿号/分隔符分隔；空字符串表示每天）。</summary>
    private static bool TryParseWeekdays(string weekdays, out List<DayOfWeek> result)
    {
        result = [];
        var trimmed = weekdays.Trim();
        if (trimmed.Length == 0) return true;

        foreach (var token in trimmed.Split([',', '，', '、', '/', ';', '；', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(token.Trim(), out var number) || number is < 1 or > 7) return false;
            var day = number == 7 ? DayOfWeek.Sunday : (DayOfWeek)number;
            if (!result.Contains(day)) result.Add(day);
        }
        return result.Count > 0;
    }

    /// <summary>把任务内容压成单行预览，供审批与结果转述。</summary>
    private static string ContentPreview(string content)
    {
        var singleLine = content.ReplaceLineEndings(" ").Trim();
        return singleLine.Length == 0 ? "(空)" : Truncate(singleLine, TaskContentPreviewLength);
    }

    private static string ApprovalDenied(CancellationToken cancellationToken, string action) =>
        cancellationToken.IsCancellationRequested
            ? $"{action}已取消。"
            : $"用户拒绝{action}，不要重试，改为告知用户已被拒绝。";

    private static string TaskMissing(string id) =>
        $"没有找到 id 为「{id}」的定时任务，请先用 list_scheduled_tasks 确认。";

    private static string UnrecognizedKind(string? kind) =>
        $"无法识别的调度方式「{kind}」，请用 once / daily / weekly / interval。";

    /// <summary>读取指定会话的最近消息；会话 id 取自平台消息前缀（如 group:12345、private:777）。</summary>
    private Task<string> GetMessages(
        [Description("消息平台 id，例如 napcat")]
        string platform,
        [Description("会话 id，形如 group:12345 或 private:777")]
        string conversation,
        [Description("最多读取多少条最近消息，默认 20")]
        int limit = 20)
        => _platforms.GetMessagesAsync(platform, conversation, limit);

    /// <summary>向指定会话发送消息。</summary>
    private Task<string> SendMessage(
        [Description("消息平台 id，例如 napcat")]
        string platform,
        [Description("会话 id，形如 group:12345 或 private:777")]
        string conversation,
        [Description("要发送的消息内容")]
        string message)
        => _platforms.SendMessageAsync(platform, conversation, message);

    /// <summary>向指定会话发送图片或文件；发送前按审批规则向用户确认。</summary>
    private async Task<string> SendAttachment(
        [Description("消息平台 id，例如 napcat")]
        string platform,
        [Description("会话 id，形如 group:12345 或 private:777")]
        string conversation,
        [Description("本地文件的绝对路径")]
        string path,
        [Description("展示文件名")]
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveLocalFile(path, out var fullPath, out var info, out var error)) return error;

        if (!await _access.RequestApprovalAsync(
                "send_attachment",
                $"平台：{platform.Trim()}\n会话：{conversation.Trim()}\n文件：{fullPath}（{FormatSize(info.Length)}）",
                cancellationToken).ConfigureAwait(false))
            return cancellationToken.IsCancellationRequested
                ? "发送已取消。"
                : "用户没有允许发送文件，文件未发送。";

        return await _platforms
            .SendAttachmentAsync(platform, conversation, fullPath, displayName)
            .ConfigureAwait(false);
    }

    /// <summary>在本地对话窗口展示图片或文件卡片。</summary>
    private string ShowFile(
        [Description("本地文件的绝对路径")]
        string path,
        [Description("展示文件名")]
        string? displayName = null)
    {
        if (!TryResolveLocalFile(path, out var fullPath, out var info, out var error)) return error;
        if (info.Length > ShowFileMaxBytes)
            return $"文件「{info.Name}」大小为 {FormatSize(info.Length)}，超过 {FormatSize(ShowFileMaxBytes)} 的展示上限。";

        var name = string.IsNullOrWhiteSpace(displayName) ? info.Name : displayName.Trim();
        var isImage = ImageExtensions.Contains(info.Extension);
        _files.Show(new ChatFileDisplay(fullPath, name, isImage));

        return isImage
            ? $"已在对话窗口展示图片「{name}」。"
            : $"已在对话窗口展示文件「{name}」。";
    }

    /// <summary>
    /// 写入文本文件：新建或整体覆盖。写入前计算行级差异并请求审批，
    /// 成功后把差异交给界面展示；内容没变化时不写盘。
    /// </summary>
    private async Task<string> WriteFile(
        [Description("文件路径：绝对路径，或相对当前会话工作区的路径")]
        string path,
        [Description("要写入的完整文本内容")]
        string content,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveWritableFile(path, out var fullPath, out var error)) return error;
        if (content.Length > MaxEditChars)
            return $"写入内容过长（{content.Length} 字符），超过 {MaxEditChars} 字符的上限。";

        string? oldText = null;
        Encoding encoding = Utf8NoBom;
        if (File.Exists(fullPath))
        {
            if (!TryReadTextFile(fullPath, out var existing, out var existingEncoding, out var readError))
                return readError;
            oldText = existing;
            encoding = existingEncoding;
        }

        if (oldText == content)
            return $"文件「{fullPath}」内容没有变化，未写入。";

        var diff = LineDiff.Compute(fullPath, oldText ?? string.Empty, content);
        var detail = BuildEditApprovalDetail(fullPath, diff, oldText is null ? "新建文件" : "写入文件");
        if (!await _access.RequestApprovalAsync("write_file", detail, cancellationToken).ConfigureAwait(false))
            return ApprovalDenied(cancellationToken, "写入文件");

        // 覆盖已有文件时把旧版本留在会话备份目录，写坏/写错还能找回
        var backupPath = oldText is null ? null : BackupPathFor(fullPath);
        string? actualBackup;
        try
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            // 临时文件 + File.Replace：中途崩溃不会把原文件截断；沿用原编码与 BOM
            actualBackup = WriteTextFile(fullPath, content, encoding, backupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            return $"写入文件失败：{ex.Message}";
        }

        RecordEdit(diff);
        var result = EditResult("已写入", fullPath, diff);
        if (actualBackup is not null) result += $"\n原文件已备份到 {actualBackup}。";
        return result;
    }

    /// <summary>
    /// 编辑文本文件：把一段旧文本替换成新文本。默认只允许唯一匹配，
    /// 多处匹配时必须显式 replace_all，避免误改。
    /// </summary>
    private async Task<string> EditFile(
        [Description("文件路径：绝对路径，或相对当前会话工作区的路径")]
        string path,
        [Description("要替换的旧文本，必须与文件内容完全一致")]
        string oldString,
        [Description("替换后的新文本")]
        string newString,
        [Description("是否替换全部匹配（默认 false；存在多个匹配时必须显式开启）")]
        bool replaceAll = false,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveWritableFile(path, out var fullPath, out var error)) return error;
        if (!File.Exists(fullPath)) return $"文件不存在：{fullPath}";
        if (!TryReadTextFile(fullPath, out var text, out var encoding, out var readError)) return readError;

        if (oldString.Length == 0) return "old_string 不能为空；要整体覆盖请用 write_file。";
        if (oldString == newString) return "新旧文本相同，未做修改。";

        var count = CountOccurrences(text, oldString);
        if (count == 0)
            return $"文件里没有找到要替换的内容，请先用 read_file 核对。old_string 开头：{Truncate(oldString, 120)}";
        if (count > 1 && !replaceAll)
            return $"old_string 在文件里出现了 {count} 次，请提供更长的上下文使其唯一，或传 replace_all=true。";

        var updated = replaceAll
            ? text.Replace(oldString, newString, StringComparison.Ordinal)
            : ReplaceFirst(text, oldString, newString);
        if (updated == text) return "替换后内容没有变化，未写入。";
        if (updated.Length > MaxEditChars)
            return $"修改后的内容超过 {MaxEditChars} 字符的上限，未写入。";

        var diff = LineDiff.Compute(fullPath, text, updated);
        var detail = BuildEditApprovalDetail(fullPath, diff, "编辑文件");
        if (!await _access.RequestApprovalAsync("edit_file", detail, cancellationToken).ConfigureAwait(false))
            return ApprovalDenied(cancellationToken, "编辑文件");

        var backupPath = BackupPathFor(fullPath);
        string? actualBackup;
        try
        {
            actualBackup = WriteTextFile(fullPath, updated, encoding, backupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            return $"写入文件失败：{ex.Message}";
        }

        RecordEdit(diff);
        var result = EditResult("已编辑", fullPath, diff);
        if (actualBackup is not null) result += $"\n原文件已备份到 {actualBackup}。";
        return result;
    }

    /// <summary>工具回给模型的写盘结果：有差异时带增删行数，过大时说明未生成 diff。</summary>
    private static string EditResult(string verb, string fullPath, ChatFileDiffState diff) =>
        diff.Truncated && diff.Hunks.Count == 0
            ? $"{verb} {fullPath}（文件过大，未生成 diff）。"
            : $"{verb} {fullPath}（+{diff.Added} −{diff.Removed}）。";

    /// <summary>把本次编辑的差异交给界面；没有界面订阅者时静默丢弃。</summary>
    private void RecordEdit(ChatFileDiffState diff)
    {
        var callId = ToolInvocationContext.CurrentCallId;
        if (callId.Length == 0) return;
        // 文件过大时也把 truncated 状态送出去，界面显示「未生成 diff」而不是什么都不显示
        if (diff.Hunks.Count == 0 && !diff.Truncated) return;
        _files.RecordEdit(new ChatFileEdit(callId, [diff]));
    }

    /// <summary>审批气泡里的编辑摘要：统计 + 前 40 行差异，长度受控。</summary>
    private static string BuildEditApprovalDetail(string fullPath, ChatFileDiffState diff, string action)
    {
        var header = $"{action}：{fullPath}";
        if (diff.Hunks.Count == 0)
            return diff.Truncated ? $"{header}\n差异过大，未生成预览。" : header;

        var builder = new StringBuilder(header);
        builder.Append("\n+").Append(diff.Added).Append(" −").Append(diff.Removed);

        var lines = 0;
        foreach (var hunk in diff.Hunks)
        {
            builder.Append("\n@@ -").Append(hunk.OldStart).Append(',').Append(hunk.OldLines)
                .Append(" +").Append(hunk.NewStart).Append(',').Append(hunk.NewLines).Append(" @@");
            foreach (var line in hunk.Lines)
            {
                if (lines++ >= 40)
                {
                    builder.Append("\n…（差异过长，已截断）");
                    return builder.ToString();
                }

                builder.Append('\n')
                    .Append(line.Kind switch { "add" => '+', "del" => '-', _ => ' ' })
                    .Append(Truncate(line.Text, 160));
            }
        }

        return builder.ToString();
    }

    /// <summary>解析可写文件路径：去掉 @ 前缀，拒绝目录；文件可以不存在（新建）。</summary>
    private static bool TryResolveWritableFile(string path, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        error = string.Empty;

        var raw = path.Trim();
        if (raw.StartsWith('@')) raw = raw[1..].Trim().Trim('"');
        if (raw.Length == 0)
        {
            error = "文件路径不能为空。";
            return false;
        }

        try
        {
            fullPath = WorkspaceContext.Resolve(raw);
        }
        catch (Exception ex)
        {
            error = $"文件路径无效：{ex.Message}";
            return false;
        }

        if (Directory.Exists(fullPath))
        {
            error = $"「{fullPath}」是目录，不能作为文件写入。";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 读取文本文件并带回原编码：支持 UTF-8（有无 BOM）、UTF-16、UTF-32；
    /// 写回时沿用该编码，避免只改一行却把整个文件换成 UTF-8 无 BOM。
    /// </summary>
    internal static bool TryReadTextFile(
        string fullPath,
        out string text,
        out Encoding encoding,
        out string error)
    {
        text = string.Empty;
        encoding = Utf8NoBom;
        error = string.Empty;

        try
        {
            var info = new FileInfo(fullPath);
            if (info.Length > MaxEditableBytes)
            {
                error = $"文件「{info.Name}」大小为 {FormatSize(info.Length)}，"
                    + $"超过可编辑上限 {FormatSize(MaxEditableBytes)}。";
                return false;
            }

            return TryDecodeText(File.ReadAllBytes(info.FullName), out text, out encoding, out error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"读取文件失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 原子写回文本文件：先写同目录临时文件，再用 <see cref="File.Replace(string, string, string?, bool)" />
    /// 替换目标。中途失败或断电时原文件保持完整，不会出现写到一半被截断。
    /// 覆盖已有文件时可在替换前把旧版本复制到 <paramref name="backupPath" />（可跨盘），返回实际备份路径。
    /// </summary>
    internal static string? WriteTextFile(
        string fullPath,
        string text,
        Encoding encoding,
        string? backupPath = null)
    {
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory)) directory = ".";

        var tempPath = Path.Combine(directory, $".souffle-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, text, encoding);

            if (!File.Exists(fullPath))
            {
                File.Move(tempPath, fullPath);
                return null;
            }

            // 备份先复制到会话目录（跨盘也能用），再原子替换目标。
            // File.Replace 的备份参数要求与目标同卷，所以这里不把会话备份直接交给它。
            if (!string.IsNullOrEmpty(backupPath)) SaveBackup(fullPath, backupPath);

            File.Replace(tempPath, fullPath, null, ignoreMetadataErrors: true);
            return string.IsNullOrEmpty(backupPath) ? null : backupPath;
        }
        finally
        {
            DeleteIfExists(tempPath);
        }
    }

    /// <summary>把旧版本完整复制到备份路径：先写同目录临时文件再改名，避免留下半份备份。</summary>
    private static void SaveBackup(string source, string backupPath)
    {
        var directory = Path.GetDirectoryName(backupPath);
        if (string.IsNullOrEmpty(directory)) directory = ".";

        Directory.CreateDirectory(directory);
        var tempBackup = Path.Combine(directory, $".souffle-{Guid.NewGuid():N}.bak.tmp");
        try
        {
            File.Copy(source, tempBackup, overwrite: false);
            File.Move(tempBackup, backupPath, overwrite: true);
        }
        finally
        {
            DeleteIfExists(tempBackup);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (!File.Exists(path)) return;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Debug(ex, "delete edit temp file failed path={Path}", path);
        }
    }

    /// <summary>同一文件的备份路径稳定：会话目录 <c>backups/</c> 下保留最近一个版本。</summary>
    internal static string? BackupPathFor(string fullPath)
    {
        var directory = ToolInvocationContext.BackupDirectory;
        if (string.IsNullOrWhiteSpace(directory)) return null;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToUpperInvariant())))[..16];
        var name = Path.GetFileName(fullPath).ToLowerInvariant();
        if (name.Length > 60) name = name[..60];
        return Path.Combine(directory, "backups", $"{name}.{hash}.bak");
    }

    /// <summary>按 BOM 识别编码并严格解码；无 BOM 时只接受 UTF-8。</summary>
    internal static bool TryDecodeText(
        byte[] bytes,
        out string text,
        out Encoding encoding,
        out string error)
    {
        text = string.Empty;
        error = string.Empty;
        var offset = 0;

        if (HasPrefix(bytes, [0xEF, 0xBB, 0xBF]))
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);
            offset = 3;
        }
        else if (HasPrefix(bytes, [0xFF, 0xFE, 0x00, 0x00]))
        {
            encoding = new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true);
            offset = 4;
        }
        else if (HasPrefix(bytes, [0x00, 0x00, 0xFE, 0xFF]))
        {
            encoding = new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: true);
            offset = 4;
        }
        else if (HasPrefix(bytes, [0xFF, 0xFE]))
        {
            encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true);
            offset = 2;
        }
        else if (HasPrefix(bytes, [0xFE, 0xFF]))
        {
            encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true);
            offset = 2;
        }
        else
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        }

        try
        {
            text = encoding.GetString(bytes, offset, bytes.Length - offset);
        }
        catch (Exception ex) when (ex is DecoderFallbackException or ArgumentException)
        {
            error = "文件不是 UTF-8 / UTF-16 文本，无法编辑。";
            return false;
        }

        if (text.Contains('\0'))
        {
            error = "文件包含 NUL 字节，不是文本文件，无法编辑。";
            return false;
        }

        return true;
    }

    private static bool HasPrefix(byte[] bytes, ReadOnlySpan<byte> prefix) =>
        bytes.AsSpan().StartsWith(prefix);

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0
            ? text
            : string.Concat(text.AsSpan(0, index), newValue, text.AsSpan(index + oldValue.Length));
    }

    /// <summary>解析并校验本地文件路径：去掉 @ 前缀，拒绝目录与不存在的文件。</summary>
    private static bool TryResolveLocalFile(
        string path,
        out string fullPath,
        out FileInfo info,
        out string error)
    {
        fullPath = string.Empty;
        info = null!;
        error = string.Empty;

        var raw = path.Trim();
        if (raw.StartsWith('@')) raw = raw[1..].Trim().Trim('"');
        if (raw.Length == 0)
        {
            error = "文件路径不能为空。";
            return false;
        }

        try
        {
            // 相对路径按当前会话的逻辑工作区解析，不能依赖进程 cwd
            fullPath = WorkspaceContext.Resolve(raw);
        }
        catch (Exception ex)
        {
            error = $"文件路径无效：{ex.Message}";
            return false;
        }

        if (Directory.Exists(fullPath))
        {
            error = $"「{fullPath}」是目录，不能作为文件使用。";
            return false;
        }

        info = new FileInfo(fullPath);
        if (info.Exists) return true;

        error = $"文件不存在：{fullPath}";
        return false;
    }

    /// <summary>
    /// 向用户提问：交给对话界面展示提问面板，等用户全部回答后把问答结果回传给模型。
    /// 停止对话时取消等待，按「用户没有回答」返回。
    /// </summary>
    private async Task<string> AskUser(
        [Description("问题列表，每个问题给 2-5 个预设选项")]
        AskUserQuestion[]? questions,
        CancellationToken cancellationToken)
    {
        var list = (questions ?? [])
            .Where(question => !string.IsNullOrWhiteSpace(question.Question))
            .Take(AskUserMaxQuestions)
            .Select(question => new UserQuestion(
                question.Question.Trim(),
                [.. (question.Options ?? [])
                    .Where(option => !string.IsNullOrWhiteSpace(option.Text))
                    .Select(option => new UserOption(
                        option.Text.Trim(),
                        string.IsNullOrWhiteSpace(option.Description) ? null : option.Description.Trim()))
                    .Take(AskUserMaxOptions)]))
            .ToList();

        if (list.Count == 0)
            return "提问失败：questions 不能为空，每个问题都要有 question 内容。";

        var answers = await _questions.AskAsync(list, cancellationToken).ConfigureAwait(false);
        if (answers is null) return "用户没有回答提问（已取消）。";

        var builder = new StringBuilder("用户已回答：");
        for (var i = 0; i < answers.Count; i++)
        {
            builder.Append('\n').Append(i + 1).Append(". ").Append(answers[i].Question)
                .Append("\n→ ").Append(answers[i].Answer);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 读取本地文件并按类型返回合适的内容：文本走文本通道（支持 offset/limit 分段读取）；
    /// 图片、音频作为 DataContent 返回，由对话服务拆成多模态内容，避免二进制被当成普通文本。
    /// 其余二进制文件只回报文件信息，不把乱码塞进提示词。
    /// </summary>
    private static AIContent[] ReadFile(
        [Description("文件绝对路径")]
        string path,
        [Description("文本起始字符位置，默认 0")]
        int offset = 0,
        [Description("最多返回的字符数，默认 20000")]
        int limit = ReadFileMaxChars)
    {
        var target = path.Trim();
        if (target.StartsWith('@')) target = target[1..].Trim().Trim('"');

        if (target.Length == 0) return [new TextContent("文件路径不能为空。")];
        if (Directory.Exists(target)) return [new TextContent($"「{target}」是目录，不能按文件读取。")];

        var info = new FileInfo(target);
        if (!info.Exists) return [new TextContent($"文件不存在：{target}")];

        var mediaType = GetReadableMediaType(info.Extension);
        if (mediaType is not null)
        {
            // 媒体文件要整份读入内存，过大直接拒绝
            if (info.Length > ReadFileMaxBytes)
                return [new TextContent($"文件「{info.Name}」大小为 {FormatSize(info.Length)}，超过 {FormatSize(ReadFileMaxBytes)} 的读取上限，未读取。")];

            var data = File.ReadAllBytes(info.FullName);
            return
            [
                new TextContent($"已读取{DescribeMedia(mediaType)}「{info.Name}」（{FormatSize(info.Length)}）。"),
                new DataContent(data, mediaType)
            ];
        }

        if (GetVideoMediaType(info.Extension) is { } videoType)
            return [new TextContent($"「{info.Name}」是视频（{FormatSize(info.Length)}，{videoType}），无法直接作为内容读取；"
                + "如需分析，可用 run_shell_command 配合 ffmpeg 抽取关键帧或音频，再读取生成的文件。")];

        offset = Math.Max(offset, 0);
        limit = Math.Clamp(limit, 1, ReadFileMaxChars);
        var start = offset;

        // 文本按分段流式读取：跳过 offset 个字符，再多读一个字符用于判断是否还有后续内容
        var builder = new StringBuilder();
        bool hasMore;
        try
        {
            using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);

            var buffer = new char[4096];
            var skip = start;
            while (skip > 0)
            {
                var read = reader.Read(buffer, 0, Math.Min(buffer.Length, skip));
                if (read <= 0) break;
                skip -= read;
            }

            while (builder.Length <= limit)
            {
                var read = reader.Read(buffer, 0, Math.Min(buffer.Length, limit + 1 - builder.Length));
                if (read <= 0) break;
                builder.Append(buffer, 0, read);
            }

            hasMore = builder.Length > limit;
            if (hasMore) builder.Length = limit;
        }
        catch (DecoderFallbackException)
        {
            return [new TextContent($"「{info.Name}」是二进制文件（{FormatSize(info.Length)}），无法按文本读取。")];
        }

        if (builder.ToString().Contains('\0'))
            return [new TextContent($"「{info.Name}」是二进制文件（{FormatSize(info.Length)}），无法按文本读取。")];

        if (builder.Length == 0)
            return [new TextContent($"已到文件末尾：「{info.Name}」共 {FormatSize(info.Length)}，起始位置 {start} 之后没有内容。")];

        var header = start == 0
            ? $"文件 {info.FullName}（{FormatSize(info.Length)}）："
            : $"文件 {info.FullName}（{FormatSize(info.Length)}，从第 {start} 个字符开始）：";
        if (hasMore)
            builder.Append($"\n…（已截断，本次返回 {limit} 字符；可传 offset={start + limit} 继续读取）");

        return [new TextContent($"{header}\n{builder}")];
    }

    /// <summary>可转成多模态内容直接交给模型的媒体类型（图片、音频）；其他返回 null。</summary>
    private static string? GetReadableMediaType(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".m4a" => "audio/mp4",
        ".aac" => "audio/aac",
        ".ogg" or ".oga" => "audio/ogg",
        ".flac" => "audio/flac",
        _ => null
    };

    /// <summary>视频没有通用的多模态通道，只用于给出提示。</summary>
    private static string? GetVideoMediaType(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".avi" => "video/x-msvideo",
        ".mkv" => "video/x-matroska",
        _ => null
    };

    private static string DescribeMedia(string mediaType) => mediaType.Split('/')[0] switch
    {
        "image" => "图片",
        "audio" => "音频",
        _ => "文件"
    };

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:0.#} MB",
        >= 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes} 字节"
    };

    private async Task<string> RunShellCommand(
        [Description(
            "要执行的 PowerShell 命令")]
        string command,
        [Description("命令超时时间（秒），默认 30，最大 1800")]
        int timeoutSeconds = DefaultShellTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var timeoutSecondsValue = Math.Clamp(timeoutSeconds, 1, MaxShellTimeoutSeconds);

        // 命令执行按当前审批规则处理：手动审批时阻塞等待用户点击允许
        if (!await _access.RequestShellApprovalAsync(command, cancellationToken))
        {
            return cancellationToken.IsCancellationRequested
                ? "命令已取消。"
                : "用户没有允许这次命令执行，命令未运行。";
        }

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            // 命令在会话工作区里执行：每个会话有各自的逻辑工作目录，不能依赖进程 cwd
            WorkingDirectory = WorkspaceContext.Current,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        // 强制 UTF-8，避免中文输出按控制台代码页解码成乱码
        startInfo.ArgumentList.Add("$OutputEncoding=[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + command);

        using var process = new Process();
        process.StartInfo = startInfo;

        // 按行异步收集输出：不能用 ReadToEndAsync，它要等管道 EOF，
        // 而命令拉起的后台进程会继承写端句柄，导致永远等不到 EOF、看起来每次都超时。
        var stdoutLines = new ConcurrentQueue<string>();
        var stderrLines = new ConcurrentQueue<string>();
        var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) stdoutClosed.TrySetResult();
            else stdoutLines.Enqueue(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) stderrClosed.TrySetResult();
            else stderrLines.Enqueue(e.Data);
        };

        // 用 Exited 事件等进程退出，不要用 WaitForExitAsync：开始异步读流之后，
        // WaitForExitAsync 会连同「读到管道 EOF」一起等，而命令拉起的 daemon
        // （比如 agent-browser）会继承写端句柄，EOF 永远不来，结果每次都超时。
        var exitTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => exitTask.TrySetResult();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSecondsValue));

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"命令启动失败：{ex.Message}";
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // 正常退出与超时只有「结束原因」不同：输出收集、收敛、拼装完全一样，
        // 所以只用一个 try 等到进程退出或被超时打断，最后按原因套不同的外壳。
        var timedOut = false;
        try
        {
            await exitTask.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                /* 已退出则忽略 */
            }

            // 只有「停止对话」才把取消继续往上抛；命令自身超时按普通结果回给模型，
            // 不能连带取消本次 LLM 请求。
            cancellationToken.ThrowIfCancellationRequested();
            timedOut = true;
        }

        // 进程已退出或已被终止：能收到 EOF 就说明输出读完，收不到也不再多等
        // （避免被继承了写端句柄的常驻进程拖住）。超时也把已收到的输出带回去：
        // CLI 常在卡住前先打印一行进度或错误，这些线索丢了就只能看到「超时」。
        await Task.WhenAny(
                Task.WhenAll(stdoutClosed.Task, stderrClosed.Task),
                Task.Delay(OutputDrainTimeout, CancellationToken.None))
            .ConfigureAwait(false);

        var stdout = TruncateOutput(JoinOutput(stdoutLines));
        var stderr = TruncateOutput(JoinOutput(stderrLines));

        var result = new StringBuilder(
            timedOut ? $"命令超过 {timeoutSecondsValue} 秒未结束，已终止。" : $"退出码 {process.ExitCode}");
        if (stdout.Length > 0) result.Append(timedOut ? "\n已产生的输出：\n" : "\n输出：\n").Append(stdout);
        if (stderr.Length > 0) result.Append(timedOut ? "\n已产生的错误：\n" : "\n错误：\n").Append(stderr);
        if (stdout.Length == 0 && stderr.Length == 0)
        {
            result.Append(timedOut
                ? "\n（没有任何输出：命令卡在启动阶段，或它拉起的常驻进程继承了输出句柄）"
                : "\n（无输出）");
        }

        return result.ToString();
    }

    private static string JoinOutput(ConcurrentQueue<string> lines) =>
        lines.IsEmpty ? string.Empty : string.Join(Environment.NewLine, lines).Trim();

    private static string TruncateOutput(string text) =>
        text.Length <= ShellOutputMaxLength ? text : text[..ShellOutputMaxLength] + "…（已截断）";
}