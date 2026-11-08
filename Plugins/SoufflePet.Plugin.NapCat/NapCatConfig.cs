// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace SoufflePet.Plugins.NapCat;

/// <summary>插件配置：从宿主注入的 JSON 读取连接参数、白名单、唤醒策略与附件设置。</summary>
internal sealed class NapCatConfig
{
    public string WsUrl { get; init; } = "ws://127.0.0.1:3001";

    public string AccessToken { get; init; } = string.Empty;

    public HashSet<long> AllowedUsers { get; init; } = [];

    public HashSet<long> AllowedGroups { get; init; } = [];

    public bool WakeOnPrivateMessage { get; init; } = true;

    /// <summary>唤醒词（仅群聊生效）：消息正文只要含有其中任意一个（不区分大小写）就唤醒桌宠（无需 @）；为空表示不启用。</summary>
    public string[] WakeWords { get; init; } = [];

    /// <summary>概率回复（仅群聊生效）：非 @、非引用桌宠、也未命中唤醒词的消息，按此概率（0-1）随机唤醒桌宠；0 表示关闭。</summary>
    public double ReplyProbability { get; init; }

    public int MaxReplyChars { get; init; } = 1500;

    /// <summary>是否把收到的图片/文件下载到本地，并把路径写进消息。</summary>
    public bool DownloadAttachments { get; init; } = true;

    /// <summary>附件缓存目录（绝对路径）。</summary>
    public string AttachmentCacheDir { get; init; } = string.Empty;

    /// <summary>平台角色说明（例如「你正在托管主人的 QQ」），注入系统提示词；为空则不注入。</summary>
    public string RolePrompt { get; init; } = string.Empty;

    /// <summary>读取插件配置；缺省值与 plugin.json 的默认段保持一致。</summary>
    public static NapCatConfig Load(JsonElement? config, string pluginDirectory)
    {
        var cacheDir = GetConfigString(config, "attachmentCacheDir", "attachments").Trim();
        if (cacheDir.Length == 0) cacheDir = "attachments";

        return new NapCatConfig
        {
            WsUrl = GetConfigString(config, "wsUrl", "ws://127.0.0.1:3001"),
            AccessToken = GetConfigString(config, "accessToken", string.Empty),
            AllowedUsers = ReadConfigIds(config, "allowedUsers"),
            AllowedGroups = ReadConfigIds(config, "allowedGroups"),
            WakeOnPrivateMessage = GetConfigBool(config, "wakeOnPrivateMessage", true),
            WakeWords = ReadConfigWords(config, "wakeWords"),
            ReplyProbability = Math.Clamp(GetConfigDouble(config, "replyProbability", 0), 0, 1),
            MaxReplyChars = Math.Clamp(GetConfigInt(config, "maxReplyChars", 1500), 200, 4000),
            DownloadAttachments = GetConfigBool(config, "downloadAttachments", true),
            RolePrompt = GetConfigString(config, "rolePrompt", string.Empty).Trim(),
            AttachmentCacheDir = Path.GetFullPath(
                Path.IsPathRooted(cacheDir) ? cacheDir : Path.Combine(pluginDirectory, cacheDir)),
        };
    }

    private static string GetConfigString(JsonElement? config, string key, string fallback)
    {
        if (config is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty(key, out var value))
            return fallback;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? fallback,
            JsonValueKind.Number => value.GetRawText(),
            _ => fallback,
        };
    }

    private static bool GetConfigBool(JsonElement? config, string key, bool fallback)
    {
        if (config is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty(key, out var value))
            return fallback;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => fallback,
        };
    }

    private static int GetConfigInt(JsonElement? config, string key, int fallback)
    {
        if (config is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty(key, out var value))
            return fallback;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => fallback,
        };
    }

    private static double GetConfigDouble(JsonElement? config, string key, double fallback)
    {
        if (config is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty(key, out var value))
            return fallback;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => fallback,
        };
    }

    /// <summary>读取 QQ 号/群号白名单：支持 config 里的数组，也兼容逗号或空白分隔的字符串。</summary>
    private static HashSet<long> ReadConfigIds(JsonElement? config, string key)
    {
        var ids = new HashSet<long>();
        if (config is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty(key, out var value))
            return ids;

        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    var raw = item.ValueKind == JsonValueKind.Number ? item.GetRawText() : item.GetString();
                    AddId(raw, ids);
                }

                break;

            case JsonValueKind.String:
                foreach (var part in (value.GetString() ?? string.Empty).Split(
                             [',', '，', ';', '；', ' ', '\n', '\r', '\t'],
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    AddId(part, ids);
                }

                break;

            default:
                AddId(value.GetRawText(), ids);
                break;
        }

        return ids;
    }

    private static void AddId(string? raw, HashSet<long> ids)
    {
        if (long.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
            ids.Add(id);
    }

    /// <summary>读取唤醒词列表：支持 config 里的数组，也兼容逗号或空白分隔的字符串；去重且忽略大小写。</summary>
    private static string[] ReadConfigWords(JsonElement? config, string key)
    {
        if (config is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty(key, out var value))
            return [];

        var words = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? raw)
        {
            var word = raw?.Trim();
            if (string.IsNullOrEmpty(word) || !seen.Add(word)) return;
            words.Add(word);
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    Add(item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText());
                break;

            case JsonValueKind.String:
                foreach (var part in (value.GetString() ?? string.Empty).Split(
                             [',', '，', ';', '；', ' ', '\n', '\r', '\t'],
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    Add(part);
                }

                break;

            default:
                Add(value.GetRawText());
                break;
        }

        return [.. words];
    }
}
