// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.ComponentModel;
using System.Text;
using System.Text.Json;
using SoufflePet.Services.Plugins;

namespace SoufflePet.Services;

/// <summary>
/// 联网搜索、统计、表情/记忆/技能、插件管理与消息平台等查询与转发型工具。
/// </summary>
public sealed partial class ChatTools
{
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

    /// <summary>列出所有消息平台当前已知的会话（群聊 / 私聊）及其会话 id。</summary>
    private Task<string> ListPlatformConversations()
        => _platforms.ListConversationsAsync();

    /// <summary>读取指定会话的最近消息；会话 id 取自平台消息前缀（如 group:12345、private:777）。</summary>
    private Task<string> GetPlatformMessages(
        [Description("消息平台 id，例如 napcat")]
        string platform,
        [Description("会话 id，形如 group:12345 或 private:777")]
        string conversation,
        [Description("最多读取多少条最近消息，默认 20")]
        int limit = 20)
        => _platforms.GetMessagesAsync(platform, conversation, limit);

    /// <summary>向指定会话发送消息。</summary>
    private Task<string> SendPlatformMessage(
        [Description("消息平台 id，例如 napcat")]
        string platform,
        [Description("会话 id，形如 group:12345 或 private:777")]
        string conversation,
        [Description("要发送的消息内容")]
        string message)
        => _platforms.SendMessageAsync(platform, conversation, message);

    /// <summary>向指定会话发送图片或文件；发送前按审批规则向用户确认。</summary>
    private async Task<string> SendPlatformAttachment(
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
}
