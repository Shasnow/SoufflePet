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
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using SoufflePet.Models;
using SoufflePet.Services.Plugins;
using SoufflePet.Utils;

namespace SoufflePet.Services;

/// <summary>
/// 对话模型可调用的本地工具集（含切换表情、执行命令这两个有副作用的工具）：
/// 由 AIFunctionFactory 依据方法签名与 <see cref="DescriptionAttribute"/> 生成工具声明，
/// 模型按需发起调用，本地执行后再把结果回传给模型。
/// </summary>
public sealed class ChatTools
{
    /// <summary>命令执行的默认超时（秒）：模型没指定 timeout_seconds 时使用。</summary>
    private const int DefaultShellTimeoutSeconds = 30;

    /// <summary>命令执行允许的最大超时（秒）：模型传得更大时按此上限处理。</summary>
    private const int MaxShellTimeoutSeconds = 600;

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

    private readonly SouffleSettings _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PetExpressionService _expressions;
    private readonly ChatToolAccess _access;
    private readonly ChatMemory _memory;
    private readonly IMcpToolService _mcp;
    private readonly ISkillService _skills;
    private readonly IPluginService _plugins;
    private readonly UserQuestionService _questions;
    private readonly PlatformService _platforms;
    private readonly ChatFileService _files;

    public ChatTools(
        SouffleSettings settings,
        IHttpClientFactory httpClientFactory,
        PetExpressionService expressions,
        ChatToolAccess access,
        ChatMemory memory,
        IMcpToolService mcp,
        ISkillService skills,
        IPluginService plugins,
        UserQuestionService questions,
        PlatformService platforms,
        ChatFileService files)
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
        Tools =
        [
            AIFunctionFactory.Create(
                GetCurrentTime,
                "get_current_time",
                "获取当前本地日期、时间、星期和时区。"),
            AIFunctionFactory.Create(
                WebSearch,
                "web_search",
                "联网搜索实时信息，返回网页标题、链接和摘要。"),
            AIFunctionFactory.Create(
                GetExpressions,
                "get_expressions",
                "列出当前 Live2D 模型可用的表情名称。"),
            AIFunctionFactory.Create(
                GetCurrentExpression,
                "get_current_expression",
                "获取当前显示的 Live2D 表情。"),
            AIFunctionFactory.Create(
                SetExpression,
                "set_expression",
                "切换桌宠的 Live2D 表情；空字符串表示恢复默认。"),
            AIFunctionFactory.Create(
                ReadFile,
                "read_file",
                "读取文件内容，支持文本、图片、音频。"),
            AIFunctionFactory.Create(
                RunShellCommand,
                "run_shell_command",
                "执行 PowerShell 命令，返回退出码与输出。"),
            AIFunctionFactory.Create(
                ListWindows,
                "list_windows",
                "列出当前屏幕上的可见窗口。"),
            AIFunctionFactory.Create(
                AskUser,
                "ask_user",
                "向用户提问以补全信息：每个问题给 2-5 个预设选项，"
                + "界面会附带一个「其他」选项；用户全部回答后返回结果。"),
            AIFunctionFactory.Create(
                TakeScreenshot,
                "take_screenshot",
                "截取屏幕画面，图片会返回给你，同时保存为 PNG。"
                + "不传 window 截取光标所在显示器；传 window 按标题截取该窗口（可不完整、不区分大小写，被遮挡也能截）。"),
            AIFunctionFactory.Create(
                Remember,
                "remember",
                "把用户信息写入长期记忆（跨会话保留）；同一个 key 会覆盖旧值。"),
            AIFunctionFactory.Create(
                Forget,
                "forget",
                "删除长期记忆里的一条记录。"),
            AIFunctionFactory.Create(
                Recall,
                "recall",
                "按关键词检索过去的对话记录。"),
            AIFunctionFactory.Create(
                ReadSkill,
                "read_skill",
                "读取某个技能的完整说明。"),
            AIFunctionFactory.Create(
                GetMessages,
                "get_messages",
                "读取消息平台某个会话（私聊或群聊）的最近消息。"),
            AIFunctionFactory.Create(
                SendMessage,
                "send_message",
                "向消息平台的某个会话（私聊或群聊）发送消息。"),
            AIFunctionFactory.Create(
                SendAttachment,
                "send_attachment",
                "向消息平台的某个会话发送本地图片或文件。"),
            AIFunctionFactory.Create(
                ShowFile,
                "show_file",
                "在本地对话窗口里直接给用户展示一张图片或一个文件卡片。")
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

        return tools;
    }

    private static string GetCurrentTime()
    {
        var now = DateTimeOffset.Now;
        var culture = CultureInfo.GetCultureInfo("zh-CN");
        return $"{now.ToString("yyyy-MM-dd HH:mm:ss dddd", culture)}，UTC{now:zzz}，时区 {TimeZoneInfo.Local.Id}";
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
        return names.Count == 0
            ? $"当前模型 {_settings.ModelName} 没有配置表情。"
            : $"当前模型 {_settings.ModelName} 的可用表情：{string.Join("、", names)}。";
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
        [Description("技能名称（取自系统提示词的技能清单）")] string name)
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

    /// <summary>读取指定会话的最近消息；会话 id 取自平台消息前缀（如 group:12345、private:777）。</summary>
    private Task<string> GetMessages(
        [Description("消息平台 id，例如 napcat")]
        string platform,
        [Description("会话 id，形如 group:12345 或 private:777，可从平台消息的 [会话 id] 前缀获取")]
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
        [Description("本地文件的绝对路径，可带 @ 前缀")]
        string path,
        [Description("展示文件名；不传时用文件名")]
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
        [Description("本地文件的绝对路径，可带 @ 前缀")]
        string path,
        [Description("展示文件名；不传时用文件名")]
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
            fullPath = Path.GetFullPath(raw);
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
        [Description("文件绝对路径，可带 @ 前缀")]
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

    /// <summary>
    /// 截图结果直接以 AIContent 集合返回：AIFunctionFactory 对可赋值给 IEnumerable&lt;T&gt; 的声明类型
    /// 不做 JSON 序列化，对话服务能据此把图片拆出来走多模态通道。
    /// </summary>
    private AIContent[] TakeScreenshot(
        [Description("窗口标题，可只写一部分；不传则截取光标所在显示器")]
        string? window = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("截图工具仅支持 Windows。");

        var target = window?.Trim();
        var shot = string.IsNullOrEmpty(target)
            ? ScreenCapture.CaptureCurrentMonitor()
            : ScreenCapture.CaptureWindow(target);
        var label = string.IsNullOrEmpty(target) ? "当前显示器" : $"窗口「{target}」";
        return
        [
            new TextContent(
                $"已截取{label}：{shot.Width}×{shot.Height} 像素，左上角屏幕坐标 ({shot.ScreenLeft}, {shot.ScreenTop})，"
                + $"文件 {shot.FilePath}。图片可能被缩小，鼠标坐标需按比例换算后加偏移。"),
            new DataContent(shot.Image, shot.MediaType)
        ];
    }

    /// <summary>列出可截图的窗口，让模型知道 window 参数该填什么。</summary>
    private static string ListWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("窗口列表仅支持 Windows。");

        var windows = ScreenCapture.GetWindows();
        if (windows.Count == 0) return "当前没有可见的窗口。";

        var builder = new StringBuilder("当前可见窗口：\n");
        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            builder.Append(i + 1).Append(". ").Append(window.Title)
                .Append('（').Append(string.IsNullOrEmpty(window.ProcessName) ? "未知进程" : window.ProcessName).Append('）');
            if (window.IsMinimized) builder.Append("（已最小化）");
            builder.Append('\n');
        }

        return builder.ToString().TrimEnd();
    }

    private async Task<string> RunShellCommand(
        [Description(
            "要执行的 PowerShell 命令，例如 Get-ChildItem C:\\ 或 Get-Process | Sort-Object CPU -Descending | Select-Object -First 5")]
        string command,
        [Description("命令超时时间（秒），默认 30，最大 600，超出按上限处理")]
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