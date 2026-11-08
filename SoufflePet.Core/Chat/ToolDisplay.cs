// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace SoufflePet.Core.Chat;

/// <summary>
/// 工具调用的展示标题：把工具名与参数 JSON 转成「运行命令 · npm test」「读取 · a.txt」这类文案。
/// 内置工具由这里的规则生成；插件工具可通过 <see cref="DisplayPropertyKey" /> 元数据提供模板
/// （例如 <c>翻译 · {text}</c>）；MCP 工具使用服务端声明的 title。未知工具返回 null，
/// 由界面回退到「调用工具：名称」。
/// </summary>
public static class ToolDisplay
{
    /// <summary>自定义标题的元数据键（<c>AIFunction.AdditionalProperties</c>）：值为模板字符串，<c>{参数名}</c> 会被替换。</summary>
    public const string DisplayPropertyKey = "display";

    /// <summary>标题里参数摘要的最大长度（字符）。</summary>
    private const int MaxSubjectLength = 60;

    /// <summary>
    /// 按工具对象与调用参数生成标题：插件自定义模板优先，其次内置规则，最后使用 MCP 服务端 title。
    /// 都没有时返回 null，由界面回退到「调用工具：名称」。
    /// </summary>
    public static string? Describe(AIFunction? function, string name, string? argumentsJson)
    {
        if (function is not null
            && function.AdditionalProperties.TryGetValue(DisplayPropertyKey, out var display)
            && display is string template
            && Expand(template, argumentsJson) is { Length: > 0 } custom)
            return custom;

        if (Describe(name, argumentsJson) is { } builtin) return builtin;

        if (function is McpClientTool { Title: { Length: > 0 } title }) return title.Trim();

        return null;
    }

    /// <summary>
    /// 按工具名与参数生成标题；工具不在规则表里、或参数无法解析出主体时返回 null。
    /// </summary>
    public static string? Describe(string? name, string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var root = Parse(argumentsJson);

        return name switch
        {
            "web_search" => Compose("搜索", ReadString(root, "query")),
            "get_expressions" => "列出表情",
            "get_current_expression" => "查看当前表情",
            "set_expression" => ReadString(root, "name") is { Length: > 0 } expression
                ? Compose("切换表情", expression)
                : "恢复默认表情",
            "read_file" => Compose("读取", FileName(ReadString(root, "path"))),
            "run_shell_command" => Compose("运行命令", ReadString(root, "command")),
            "list_windows" => "列出窗口",
            "ask_user" => Compose("提问", FirstQuestion(root)),
            "take_screenshot" => Compose("截图", Clip(ReadString(root, "window")) ?? "当前显示器"),
            "remember" => Compose("记住", ReadString(root, "key")),
            "forget" => Compose("忘记", ReadString(root, "key")),
            "recall" => Compose("回忆", ReadString(root, "query")),
            "read_skill" => Compose("读取技能", ReadString(root, "name")),
            "list_plugins" => "列出插件",
            "set_plugin_enabled" => Compose(
                ReadString(root, "enabled") == "false" ? "停用插件" : "启用插件",
                ReadString(root, "pluginId")),
            "reload_plugins" => "重新扫描插件",
            "list_platform_conversations" => "列出平台会话",
            "get_platform_messages" => Compose("读取消息", ReadString(root, "conversation")),
            "get_session_history" => ReadString(root, "sessionId") is { Length: > 0 } session
                ? Compose("读取会话", session)
                : "列出会话",
            "send_platform_message" => Compose("发送消息", ReadString(root, "conversation")),
            "send_attachment" => Compose("发送文件",
                Clip(ReadString(root, "displayName")) ?? FileName(ReadString(root, "path"))),
            "show_file" => Compose("展示文件",
                Clip(ReadString(root, "displayName")) ?? FileName(ReadString(root, "path"))),
            "write_file" => Compose("写入", FileName(ReadString(root, "path"))),
            "edit_file" => Compose("编辑", FileName(ReadString(root, "path"))),
            "search_files" => Compose("查找", ReadString(root, "query")),
            "find_files" => Compose("查找文件", ReadString(root, "pattern")),
            "list_scheduled_tasks" => "列出定时任务",
            "create_scheduled_task" => Compose("新建任务", ReadString(root, "name")),
            "update_scheduled_task" => Compose("修改任务", ReadString(root, "id")),
            "delete_scheduled_task" => Compose("删除任务", ReadString(root, "id")),
            "get_statistics" => Compose("查询统计", ReadString(root, "item")),
            "browser_open" => Compose("打开网页", ReadString(root, "url")),
            "browser_snapshot" => "读取网页",
            "browser_click" => Compose("点击", ReadString(root, "reference")),
            "browser_type" => Compose("输入", ReadString(root, "reference")),
            "browser_press_key" => Compose("按键", ReadString(root, "key")),
            "browser_scroll" => Compose("滚动", Clip(ReadString(root, "reference")) ?? "网页"),
            "browser_back" => "返回上一页",
            "browser_close" => "关闭浏览器",
            "browser_screenshot" => "截取网页",
            _ => null,
        };
    }

    /// <summary>
    /// 展开插件提供的标题模板：<c>{参数名}</c> 替换成调用参数里对应的字符串（截断后）。
    /// 模板为空或展开后没有内容时返回 null。
    /// </summary>
    public static string? Expand(string? template, string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;

        var root = Parse(argumentsJson);
        var builder = new StringBuilder(template.Length);
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{')
            {
                builder.Append(template[i]);
                continue;
            }

            var close = template.IndexOf('}', i + 1);
            if (close < 0)
            {
                builder.Append(template[i]);
                continue;
            }

            var key = template[(i + 1)..close];
            if (Clip(ReadString(root, key)) is { } value) builder.Append(value);
            i = close;
        }

        var result = builder.ToString().Trim().TrimEnd('·', ' ', '\t');
        return result.Length == 0 ? null : result;
    }

    /// <summary>把动词与参数摘要拼成标题；摘要为空时只保留动词。</summary>
    private static string Compose(string verb, string? subject) =>
        Clip(subject) is { } value ? $"{verb} · {value}" : verb;

    /// <summary>把参数值压成单行、折叠连续空白并按需截断。</summary>
    private static string? Clip(string? text, int max = MaxSubjectLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var single = text.ReplaceLineEndings(" ").Trim();
        var builder = new StringBuilder(single.Length);
        var lastWasSpace = false;
        foreach (var ch in single)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace) builder.Append(' ');
                lastWasSpace = true;
                continue;
            }

            builder.Append(ch);
            lastWasSpace = false;
        }

        var value = builder.ToString();
        return value.Length <= max ? value : value[..max] + "…";
    }

    /// <summary>只取文件名：绝对路径在标题里没有信息量，也容易撑满一行。</summary>
    private static string? FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var value = path.Trim();
        if (value.StartsWith('@')) value = value[1..].Trim().Trim('"');
        var index = value.LastIndexOfAny(['\\', '/']);
        return Clip(index >= 0 ? value[(index + 1)..] : value);
    }

    /// <summary>ask_user 的标题取第一道问题。</summary>
    private static string? FirstQuestion(JsonElement? root)
    {
        if (root is not { ValueKind: JsonValueKind.Object } element
            || !element.TryGetProperty("questions", out var questions)
            || questions.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var question in questions.EnumerateArray())
        {
            if (ReadString(question, "question") is { } text) return Clip(text);
        }

        return null;
    }

    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement? root, string property)
    {
        if (root is not { ValueKind: JsonValueKind.Object } element
            || !element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }
}