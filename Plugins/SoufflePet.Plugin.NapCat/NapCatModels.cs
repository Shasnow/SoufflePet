// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;

namespace SoufflePet.Plugins.NapCat;

/// <summary>解析出的消息正文、是否 @/引用桌宠、首条被引用消息 id，以及待下载的附件。</summary>
internal sealed record NapCatParsedMessage(
    string Text,
    bool MentionsSelf,
    string? QuotedId,
    IReadOnlyList<NapCatPendingAttachment>? Attachments = null);

/// <summary>正文里的附件占位符、下载地址，以及下载失败时退回的文本。</summary>
internal sealed record NapCatPendingAttachment(
    string Placeholder,
    string Url,
    string Label,
    string Prefix,
    string? FileName,
    string Fallback);

/// <summary>被引用消息的摘要信息。</summary>
internal sealed record NapCatQuotedMessage(bool IsSelf, string Sender, string Summary);

/// <summary>落盘的会话缓存。</summary>
internal sealed class NapCatConversationCache
{
    public List<NapCatConversationCacheEntry> Conversations { get; set; } = [];
}

internal sealed class NapCatConversationCacheEntry
{
    public string Id { get; set; } = string.Empty;

    public string? Name { get; set; }

    public DateTimeOffset LastActive { get; set; }

    public List<string> Messages { get; set; } = [];
}
