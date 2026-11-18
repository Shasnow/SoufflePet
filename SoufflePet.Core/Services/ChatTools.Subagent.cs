// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace SoufflePet.Services;

/// <summary>
/// 子代理工具：子代理运行在独立子会话里。subagent 创建后立即返回，
/// wait_agent 等待结果，send_message 继续给它派活。
/// </summary>
public sealed partial class ChatTools
{
    private Task<string> SpawnSubagent(
        [Description("交给子代理执行的完整提示词：写清目标、范围与期望的返回格式")]
        string prompt,
        [Description("一句话简述本次子代理任务，作为子会话标题与工具气泡展示")]
        string description,
        [Description("子代理名称（可选，便于之后用名称指定它）")]
        string? name = null,
        [Description("子代理使用的模型 key（可选，留空沿用父会话的模型）")]
        string? modelKey = null,
        CancellationToken cancellationToken = default) =>
        _subagents.SpawnAsync(new SubagentRequest(prompt, description, name, modelKey), cancellationToken);

    private Task<string> WaitForAgent(
        [Description("要等待的子代理名称或会话 id（可选；留空等待全部直接子代理）")]
        string? agent = null,
        [Description("最长等待毫秒数，默认 30000，允许范围 10000–3600000")]
        int timeoutMs = 30000,
        CancellationToken cancellationToken = default) =>
        _subagents.WaitAsync(agent, timeoutMs, cancellationToken);

    private Task<string> SendMessageToAgent(
        [Description("子代理名称或会话 id")]
        string agent,
        [Description("要发送给子代理的消息；子代理正在执行时会排队")]
        string message,
        CancellationToken cancellationToken = default) =>
        _subagents.SendAsync(agent, message, cancellationToken);
}
