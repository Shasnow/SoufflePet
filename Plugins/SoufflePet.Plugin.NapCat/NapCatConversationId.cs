// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.Globalization;

namespace SoufflePet.Plugins.NapCat;

/// <summary>会话 id 的规范化与解析：统一使用 group:群号 / private:QQ号。</summary>
internal static class NapCatConversationId
{
    /// <summary>把会话 id 规范成 kind:id（小写 kind），无法识别时返回空串。</summary>
    public static string Normalize(string conversationId)
        => TryParse(conversationId, out var conversation)
            ? $"{conversation.Kind}:{conversation.Id.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;

    /// <summary>解析会话 id：支持 group:群号 / private:QQ号，也兼容 群 / 私聊 前缀以及纯数字（按私聊处理）。</summary>
    public static bool TryParse(string conversationId, out (string Kind, long Id) conversation)
    {
        conversation = default;
        var raw = conversationId?.Trim() ?? string.Empty;
        if (raw.Length == 0) return false;

        var separator = raw.IndexOfAny([':', '：']);
        var kindText = separator < 0 ? "private" : raw[..separator].Trim();
        var numberText = separator < 0 ? raw : raw[(separator + 1)..].Trim();

        var kind = kindText.ToLowerInvariant() switch
        {
            "group" or "群" or "群聊" => "group",
            "private" or "user" or "私聊" or "用户" => "private",
            _ => string.Empty,
        };

        if (kind.Length == 0) return false;
        if (!long.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
            return false;

        conversation = (kind, id);
        return true;
    }
}
