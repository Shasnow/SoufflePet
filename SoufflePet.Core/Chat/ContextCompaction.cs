// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using Microsoft.Extensions.AI;
using SoufflePet.Services;

namespace SoufflePet.Chat;

/// <summary>
/// 上下文压缩的纯逻辑：水位判断、按 token 预算确定拆分点、摘要包装、把摘要挂回历史、压缩指令的生成。
/// 只依赖静态的 <see cref="TokenEstimator" />（同样无状态），便于单测；
/// 真正的模型调用与历史替换由 <c>ChatSession</c> 负责。
/// </summary>
public static class ContextCompaction
{
    /// <summary>占用达到窗口的这个比例即触发压缩。</summary>
    public const double TriggerRatio = 0.9;

    /// <summary>为模型输出预留的 token：避免请求本身把窗口顶满、拿不到回复。</summary>
    public const int ReservedOutputTokens = 4096;

    /// <summary>压缩后保留原文的预算：约占窗口的一半，另一半留给摘要、系统提示词与本轮回复。</summary>
    public const double OriginalKeepRatio = 0.5;

    /// <summary>保留原文预算的下限：窗口很小时也留出一段最近的原文，避免历史只剩一段摘要。</summary>
    public const int MinKeepTokens = 2048;

    /// <summary>按窗口算出保留原文的 token 预算；窗口未知（0）时返回 0。</summary>
    public static int KeepTokens(int windowTokens) =>
        windowTokens <= 0 ? 0 : Math.Max(MinKeepTokens, (int)(windowTokens * OriginalKeepRatio));

    /// <summary>
    /// 按 token 预算确定「摘要」与「保留原文」的分界：从末尾往前累加到预算为止。
    /// 分界只落在用户消息上，所以裁剪后的历史仍以提问开头，工具调用与结果也不会被拆散。
    /// 返回分界处的下标（<c>history[index..]</c> 是要保留的原文）；返回 0 表示没有可摘要的部分
    /// ——全部内容都在预算内，或整段历史只有一个提问。
    /// 单轮内容自身超出预算时仍从这一轮开始保留：拆掉本轮提问只会让请求本身不可用。
    /// </summary>
    public static int SplitIndex(IReadOnlyList<ChatMessage> history, int keepTokens)
    {
        if (history.Count == 0) return 0;

        var total = 0;
        var split = -1;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            total += TokenEstimator.EstimateMessages([history[i]]);
            if (history[i].Role != ChatRole.User) continue;
            if (total > keepTokens) break;
            split = i;
        }

        if (split >= 0) return split;

        // 预算连最近一轮都放不下：仍然只保留最近一轮提问
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == ChatRole.User) return i;
        }

        return 0;
    }

    /// <summary>摘要块使用的 XML 标记名；系统提示词据此向模型解释它的含义。</summary>
    public const string SummaryTag = "conversation_summary";

    /// <summary>触发自动压缩的 token 数：窗口的 90% 再减去为输出预留的部分。</summary>
    public static int TriggerTokens(int windowTokens) =>
        windowTokens <= 0
            ? 0
            : Math.Max(0, (int)Math.Ceiling(windowTokens * TriggerRatio) - ReservedOutputTokens);

    /// <summary>估算占用是否已达到触发线（窗口未知时永不触发）。</summary>
    public static bool ShouldCompress(int estimatedTokens, int windowTokens)
    {
        if (windowTokens <= 0) return false;
        return estimatedTokens >= TriggerTokens(windowTokens);
    }

    /// <summary>把摘要正文包成 <c>&lt;conversation_summary&gt;</c> 块。</summary>
    public static string WrapSummary(string summary) =>
        $"<{SummaryTag}>\n{summary.Trim()}\n</{SummaryTag}>";

    /// <summary>
    /// 把摘要块挂到历史的开头：优先前置拼接到第一条用户消息的正文前，
    /// 这样不会出现「连续两条 user」或「历史首条是 assistant」，兼容只接受严格角色交替的接口。
    /// 历史里没有用户消息时，在头部插入一条新的用户消息承载摘要。
    /// </summary>
    public static List<ChatMessage> AttachSummary(IReadOnlyList<ChatMessage> history, string summaryBlock)
    {
        var result = new List<ChatMessage>(history.Count + 1);

        var index = 0;
        while (index < history.Count && history[index].Role != ChatRole.User) index++;

        if (index == history.Count)
        {
            result.Add(new ChatMessage(ChatRole.User, summaryBlock));
            result.AddRange(history);
            return result;
        }

        var original = history[index];
        var text = $"{summaryBlock}\n\n{original.Text}";
        List<AIContent> contents = [new TextContent(text)];
        foreach (var content in original.Contents)
        {
            // 文本由上面合并后的新正文承载，这里只保留图片等非文本内容
            if (content is not TextContent) contents.Add(content);
        }

        for (var i = 0; i < index; i++) result.Add(history[i]);
        result.Add(new ChatMessage(ChatRole.User, contents));
        for (var i = index + 1; i < history.Count; i++) result.Add(history[i]);
        return result;
    }

    /// <summary>
    /// 压缩指令：完整对话历史已原样放在前面，这里只描述摘要结构（Markdown、不限长度、不调用工具）。
    /// 摘要要能替代被丢弃的历史继续支撑后续对话，所以按固定的几节输出，避免漏掉待办与关键事实。
    /// </summary>
    public static string BuildSummaryInstruction() =>
        """
        <compress_task>
        请阅读上面的完整对话，把它压缩成一份结构化摘要，供后续对话继续使用。
        按下面的结构输出 Markdown，标题与顺序保持一致；某一节没有内容时写「无」：

        ## Primary Request and Intent
        用户的核心目标，以及明确提出的要求、偏好与约束（尽量保留原话）。

        ## Key Technical Concepts
        涉及的技术栈、框架、关键机制与设计决策。

        ## Files and Code
        涉及的文件路径、类/函数/配置项等标识，以及改了什么、关键代码片段或参数。

        ## Errors and Fixes
        遇到的问题、根因与最终解决方式；仍未解决的也要记下。

        ## Pending Jobs
        尚未完成的任务与待办。

        ## Current Work
        当前正在做的工作与所处状态。

        ## Next Step
        接下来要做的事。

        ## Critical Context
        其他继续对话必须知道的事实（重要路径、命令、数值、环境限制等）。

        要求：
        - 不要编造未出现的信息；
        - 只输出摘要正文，无需调用任何工具。
        </compress_task>
        """;
}

