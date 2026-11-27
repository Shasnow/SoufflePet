// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.Collections.Generic;
using Microsoft.Extensions.AI;

namespace SoufflePet.Services;

/// <summary>
/// 粗略的 token 估算：不依赖 tokenizer，中文/日文/韩文与全角字符按 1 字符 ≈ 1 token，
/// 其余按 4 字符 ≈ 1 token；思考内容按正文同样折算，图片/音频等媒体按固定开销计。
/// 只用于「上下文窗口水位」判断，留有余量吸收误差。
/// </summary>
public static class TokenEstimator
{
    /// <summary>每条消息的固定开销（角色、分隔符等）。</summary>
    private const int MessageOverhead = 4;

    /// <summary>
    /// 一张图/一段音频的固定开销：媒体走 DataContent 多模态通道，没有可折算的字符数，
    /// 按 base64 长度估会把一张图算成几十万 token。取一个常见视觉模型的中位开销。
    /// </summary>
    private const int MediaTokens = 1024;

    /// <summary>估算一段文本的 token 数。</summary>
    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var wide = 0;
        var narrow = 0;
        foreach (var ch in text)
        {
            if (IsWide(ch)) wide++;
            else narrow++;
        }

        // 窄字符按 4 个一组向上取整，避免长英文被算得过低
        return wide + (narrow + 3) / 4;
    }

    /// <summary>
    /// 估算一组消息的 token 数：正文、思考内容、工具调用的参数与结果、媒体内容的固定开销、
    /// 以及每条消息的固定开销。<see cref="ChatMessage.Text" /> 只含文本片段，
    /// 思考内容会以 <c>reasoning_content</c> 每轮重新发给模型、媒体也会随请求发送，都必须计入。
    /// </summary>
    public static int EstimateMessages(IEnumerable<ChatMessage> messages)
    {
        var total = 0;
        foreach (var message in messages)
        {
            total += MessageOverhead + Estimate(message.Text);
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextReasoningContent reasoning:
                        total += Estimate(reasoning.Text);
                        break;
                    case DataContent:
                    case UriContent:
                        total += MediaTokens;
                        break;
                    case FunctionCallContent call:
                        total += Estimate(call.Name) + EstimateArguments(call.Arguments);
                        break;
                    case FunctionResultContent result:
                        total += Estimate(result.Result?.ToString());
                        break;
                }
            }
        }

        return total;
    }

    private static int EstimateArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0) return 0;

        var total = 0;
        foreach (var (key, value) in arguments)
            total += Estimate(key) + Estimate(value?.ToString());
        return total;
    }

    /// <summary>是否为「一个字符大致一个 token」的宽字符（CJK、假名、谚文、全角符号）。</summary>
    private static bool IsWide(char ch) => ch switch
    {
        >= '\u1100' and <= '\u11FF' => true, // 谚文字母
        >= '\u2E80' and <= '\u303F' => true, // CJK 部首、标点
        >= '\u3040' and <= '\u30FF' => true, // 平假名、片假名
        >= '\u3400' and <= '\u4DBF' => true, // CJK 扩展 A
        >= '\u4E00' and <= '\u9FFF' => true, // CJK 基本区
        >= '\uAC00' and <= '\uD7AF' => true, // 谚文音节
        >= '\uF900' and <= '\uFAFF' => true, // CJK 兼容表意文字
        >= '\uFF00' and <= '\uFFEF' => true, // 全角字符
        _ => false,
    };
}
