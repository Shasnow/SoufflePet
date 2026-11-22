// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

namespace SoufflePet.Services;

/// <summary>
/// 平台消息相关的模型文案：系统提示词与工具参数描述共用同一份措辞。
/// 正文已改为「纯正文 + 结构化字段」（<c>&lt;platform_message conversation="…"&gt;</c>），
/// 会话 id 不再写在正文里，所以「从哪儿拿会话 id」这唯一事实源只能有一处，
/// 否则提示词与工具描述会各自漂移，模型要么回错会话、要么不敢回复。
/// </summary>
internal static class PlatformPromptHints
{
    /// <summary>工具参数 <c>conversation</c> 的说明（AIFunctionFactory 会把 Description 交给模型）。</summary>
    internal const string ConversationParameter = "会话 id，形如 group:12345 或 private:777";

    /// <summary>
    /// 系统提示词里「消息平台接入」一节的收尾说明：先点明消息平台是什么，再解释
    /// <c>&lt;platform_message&gt;</c> 的各部分，尤其是 conversation 属性的来源与回复方式。
    /// 静态文本，不随轮次变化，不影响前缀缓存。
    /// </summary>
    internal const string PlatformMessageGuide = """
        消息平台是外部会话渠道（如 QQ、微信），来自消息平台的消息将包含在 `<platform_message>` 标记中。

        平台消息标记 `<platform_message>`:
        - `conversation`: 该消息所属会话 id
        - `sender`: 发送者昵称
        - `mentions_self="true"`: 这条消息提及了你
        - `quote`: 被引用消息的摘要
        - `<context>`: 该会话的上下文；`<content>` 是本次需要回应的消息

        信息不足时用 `get_platform_messages` 工具查看该会话更早的消息；
        决定回复时，你**必须**用 `send_platform_message` 工具回复该会话。
        """;
}
