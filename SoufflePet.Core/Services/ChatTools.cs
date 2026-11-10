// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using Serilog;
using SoufflePet.Models;
using SoufflePet.Services.Plugins;

namespace SoufflePet.Services;

/// <summary>
/// 对话模型可调用的本地工具集（含切换表情、执行命令这两个有副作用的工具）：
/// 由 AIFunctionFactory 依据方法签名与 <see cref="DescriptionAttribute"/> 生成工具声明，
/// 模型按需发起调用，本地执行后再把结果回传给模型。
/// </summary>
public sealed partial class ChatTools
{
    private static readonly ILogger Logger = Log.ForContext<ChatTools>();

    /// <summary>命令执行的默认超时（秒）：模型没指定 timeout_seconds 时使用。</summary>
    private const int DefaultShellTimeoutSeconds = 30;

    /// <summary>命令执行允许的最大超时（秒）：模型传得更大时按此上限处理。</summary>
    private const int MaxShellTimeoutSeconds = 1800;

    /// <summary>命令输出回传给模型的最大长度，超出截断。</summary>
    private const int ShellOutputMaxLength = 32768;

    /// <summary>
    /// 主进程退出后等待管道 EOF 的宽限时间：命令拉起后台进程（如浏览器 daemon）时，
    /// 管道写端仍被其持有，EOF 永远不会到来，只能收完已缓冲的输出就返回。
    /// </summary>
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>媒体文件（图片/音频）读入内存的最大字节数，超出只回报文件信息、不读内容。</summary>
    private const long ReadFileMaxBytes = 20L * 1024 * 1024;

    /// <summary>内容搜索时单个文件的大小上限：超过则不扫描，避免一次搜索读入过多内容。</summary>
    private const long SearchFileMaxBytes = 1L * 1024 * 1024;

    /// <summary>内容搜索默认返回条数与上限。</summary>
    private const int SearchDefaultMaxResults = 60;
    private const int SearchMaxResults = 300;

    /// <summary>文件名查找默认返回条数与上限。</summary>
    private const int FindFilesDefaultMaxResults = 100;
    private const int FindFilesMaxResults = 300;

    /// <summary>内容搜索最多扫描的文件数，防止在超大目录里长时间运行。</summary>
    private const int SearchMaxFiles = 4000;

    /// <summary>搜索时跳过这些目录（版本库与构建产物）。</summary>
    private static readonly HashSet<string> SearchSkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", "node_modules", "bin", "obj", ".vs", ".idea", "dist", "build", "coverage"
    };

    /// <summary>读取文本文件时默认与允许返回的最大行数。</summary>
    private const int ReadFileDefaultLines = 2000;
    private const int ReadFileMaxLines = 8000;

    /// <summary>单行过长时的截断字符数，防止压缩代码 / 单行 JSON 一次占满上下文。</summary>
    private const int ReadFileMaxLineChars = 16384;

    /// <summary>读取其他会话记录时默认与允许的最大轮数。</summary>
    private const int SessionHistoryDefaultTurns = 20;
    private const int SessionHistoryMaxTurns = 200;

    /// <summary>会话记录里单条消息与会话列表预览的最大长度。</summary>
    private const int SessionHistoryMessageMaxLength = 2000;
    private const int SessionHistoryPreviewLength = 80;

    /// <summary>会话记录整体回传给模型的最大长度，超出截断。</summary>
    private const int SessionHistoryOutputMaxLength = 12000;

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
                ListPlatformConversations,
                "list_platform_conversations",
                "列出所有消息平台上当前已知的会话（群聊 / 私聊）及其会话 id"),
            AIFunctionFactory.Create(
                GetSessionHistory,
                "get_session_history",
                "列出本地所有会话，或读取指定会话的最近 / 指定日期的对话记录"),
            AIFunctionFactory.Create(
                GetPlatformMessages,
                "get_platform_messages",
                "读取消息平台某个会话的最近消息"),
            AIFunctionFactory.Create(
                SendPlatformMessage,
                "send_platform_message",
                "向消息平台的某个会话发送消息"),
            AIFunctionFactory.Create(
                SendPlatformAttachment,
                "send_platform_attachment",
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
                SearchFiles,
                "search_files",
                "在文件内容里查找关键词或正则，返回文件、行号与匹配行"),
            AIFunctionFactory.Create(
                FindFiles,
                "find_files",
                "按文件名/相对路径通配符查找文件，返回相对路径列表"),
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

}
