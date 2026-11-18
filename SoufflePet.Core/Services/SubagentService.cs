// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SoufflePet.Chat;
using SoufflePet.Core.Chat;
using SoufflePet.Models;

namespace SoufflePet.Services;

/// <summary>一次子代理调用的请求：<see cref="Prompt" /> 是真正交给子代理的提示词，<see cref="Description" /> 只用于展示。</summary>
/// <param name="Prompt">子代理要执行的完整提示词。</param>
/// <param name="Description">一句话简述，用于工具调用气泡展示。</param>
/// <param name="Name">子代理名称（可选，作为子会话标题）。</param>
/// <param name="ModelKey">使用的模型 key（可选，留空沿用父会话的模型）。</param>
public sealed record SubagentRequest(
    string Prompt,
    string Description,
    string? Name = null,
    string? ModelKey = null);

/// <summary>
/// 子代理服务：子代理不再内联执行，而是在独立子会话里后台运行。
/// 工具只负责创建 / 等待 / 追加消息，执行过程完整落在子会话的对话里，父模型只拿到结论。
/// </summary>
public interface ISubagentService
{
    /// <summary>创建子会话并立即返回；子代理在后台继续执行。</summary>
    Task<string> SpawnAsync(SubagentRequest request, CancellationToken cancellationToken);

    /// <summary>等待子代理当前这一轮结束。agent 为空时等待全部直接子代理。</summary>
    Task<string> WaitAsync(string? agent, int timeoutMs, CancellationToken cancellationToken);

    /// <summary>继续给子代理发消息：空闲时开新一轮，执行中则排队。</summary>
    Task<string> SendAsync(string agent, string message, CancellationToken cancellationToken);
}

/// <summary>
/// 子代理调度：借助 <see cref="ChatSessionManager" /> 在父会话下创建子会话，
/// 子代理的思考、工具调用与正文全部写入子会话，因此界面切进子会话即可看到完整过程。
/// 深度按父子会话链计算，避免子代理无限派生自身。
/// </summary>
public sealed class SubagentService(SouffleSettings settings, Func<ChatSessionManager> sessionManager)
    : ISubagentService
{
    private static readonly ILogger Logger = Log.ForContext<SubagentService>();

    private const int DefaultWaitTimeoutMs = 30_000;
    private const int MinWaitTimeoutMs = 10_000;
    private const int MaxWaitTimeoutMs = 3_600_000;
    private const int NameLength = 30;
    private const int ResultTextMaxLength = 8000;

    public async Task<string> SpawnAsync(SubagentRequest request, CancellationToken cancellationToken)
    {
        var prompt = request.Prompt?.Trim() ?? string.Empty;
        if (prompt.Length == 0) return "子代理提示词为空，未创建子代理。";

        if (ChatSession.CurrentTurnId is not { Length: > 0 } parentId)
            return "当前没有可挂载的会话，无法创建子代理。";

        var manager = sessionManager();
        if (manager.DepthOf(parentId) >= Math.Max(1, settings.MaxSubagentDepth))
            return "已达到子代理最大嵌套深度，未创建新子代理。";

        if (!HasUsableModel(request.ModelKey))
            return "子代理无法执行：没有可用的对话模型。";

        var name = ResolveName(request);
        SessionRecord record;
        try
        {
            record = await manager.CreateChildAsync(parentId, name, request.ModelKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "create subagent session failed parent={Parent}", parentId);
            return $"创建子代理失败：{ex.Message}";
        }

        try
        {
            var child = await manager.ResolveAsync(record.Id, cancellationToken).ConfigureAwait(false);
            // SendAsync 只启动一轮就返回，子代理在后台继续执行，创建结果立即可见。
            await child.SendAsync(prompt, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "start subagent failed session={Session}", record.Id);
            try
            {
                await manager.DeleteAsync(record.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception cleanup)
            {
                Logger.Debug(cleanup, "delete failed subagent session={Session}", record.Id);
            }

            return $"子代理启动失败：{ex.Message}";
        }

        Logger.Information("subagent spawned session={Session} parent={Parent} name={Name}", record.Id, parentId, name);
        // 结构化返回：模型能拿到会话 id，界面也能据此渲染「查看子会话」入口
        return JsonSerializer.Serialize(new
        {
            status = "started",
            sessionId = record.Id,
            name,
            hint = "用 wait_agent 等待结果，或 send_message 继续给它发消息",
        });
    }

    public async Task<string> WaitAsync(string? agent, int timeoutMs, CancellationToken cancellationToken)
    {
        if (ChatSession.CurrentTurnId is not { Length: > 0 } parentId)
            return "当前没有可等待的子代理。";

        var manager = sessionManager();
        var (targets, error) = ResolveTargets(manager, parentId, agent);
        if (error is { } message) return message;
        if (targets.Count == 0)
            return "当前会话还没有创建过子代理。";

        var sessions = new List<ChatSession>(targets.Count);
        foreach (var record in targets)
            sessions.Add(await manager.ResolveAsync(record.Id, cancellationToken).ConfigureAwait(false));

        var timeout = TimeSpan.FromMilliseconds(
            Math.Clamp(timeoutMs <= 0 ? DefaultWaitTimeoutMs : timeoutMs, MinWaitTimeoutMs, MaxWaitTimeoutMs));

        var all = Task.WhenAll(sessions.Select(session => session.WaitForIdleAsync()));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var finished = await Task.WhenAny(all, Task.Delay(timeout, timeoutCts.Token)).ConfigureAwait(false) == all;
        timeoutCts.Cancel();

        // 等待本身被取消（用户停止）时不要让调用方误以为只是超时
        cancellationToken.ThrowIfCancellationRequested();

        var builder = new StringBuilder();
        builder.AppendLine(finished
            ? "子代理已完成："
            : $"等待超时（{timeout.TotalSeconds:0} 秒），子代理仍在执行，先返回当前结果：");

        for (var index = 0; index < targets.Count; index++)
        {
            var record = targets[index];
            // 取「最后一轮提问的回答」：新一轮没有产出时不会把上一轮结论当成本轮结果
            var text = sessions[index].LastAnswerText?.Trim();
            if (text is { Length: > ResultTextMaxLength }) text = text[..ResultTextMaxLength] + "…";
            builder.Append("- ").Append(record.Title).Append("：")
                .AppendLine(string.IsNullOrEmpty(text) ? "（还没有输出）" : text.ReplaceLineEndings("\n  "));
        }

        return builder.ToString().TrimEnd();
    }

    public async Task<string> SendAsync(string agent, string message, CancellationToken cancellationToken)
    {
        message = message?.Trim() ?? string.Empty;
        if (message.Length == 0) return "消息不能为空。";

        if (ChatSession.CurrentTurnId is not { Length: > 0 } parentId)
            return "当前没有可发送消息的子代理。";

        if (string.IsNullOrWhiteSpace(agent)) return "请用子代理名称或会话 id 指定发送目标。";

        var manager = sessionManager();
        var (targets, error) = ResolveTargets(manager, parentId, agent);
        if (error is { } message2) return message2;
        if (targets.FirstOrDefault() is not { } target) return $"没有找到子代理「{agent.Trim()}」。";

        var session = await manager.ResolveAsync(target.Id, cancellationToken).ConfigureAwait(false);
        if (session.IsBusy)
        {
            await session.EnqueueSystemTurnAsync(message, ChatSession.PreviewNotice(message), isPlatformNotice: false)
                .ConfigureAwait(false);
            return $"子代理「{target.Title}」正在执行，消息已排队，会在当前轮结束后处理。";
        }

        try
        {
            await session.SendAsync(message, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"给子代理「{target.Title}」发送消息失败：{ex.Message}";
        }

        return $"已给子代理「{target.Title}」发送消息并开始新一轮，用 wait_agent 等待结果。";
    }

    /// <summary>
    /// 按名称 / id 找直接子代理：id 精确 → 标题精确 → 标题包含，逐级降级；
    /// 某一级命中多个时报歧义，要求调用方改用会话 id，避免把消息发给错误的子代理。
    /// 名称为空时返回全部直接子代理。
    /// </summary>
    private static (List<SessionRecord> Targets, string? Error) ResolveTargets(
        ChatSessionManager manager,
        string parentId,
        string? agent)
    {
        var children = manager.ChildSessions(parentId).ToList();
        var key = agent?.Trim();
        if (string.IsNullOrEmpty(key)) return (children, null);

        foreach (var matches in new[]
                 {
                     children.Where(record => string.Equals(record.Id, key, StringComparison.Ordinal)).ToList(),
                     children.Where(record => string.Equals(record.Title, key, StringComparison.OrdinalIgnoreCase))
                         .ToList(),
                     children.Where(record => record.Title.Contains(key, StringComparison.OrdinalIgnoreCase)).ToList(),
                 })
        {
            if (matches.Count == 1) return (matches, null);
            if (matches.Count > 1)
                return ([], $"有 {matches.Count} 个子代理匹配「{key}」，请改用会话 id 指定。");
        }

        return ([], $"没有找到子代理「{key}」。");
    }

    private ChatModelConfig? ResolveModel(string? modelKey)
    {
        var key = string.IsNullOrWhiteSpace(modelKey) ? settings.ChatModelKey : modelKey.Trim();
        var model = settings.ChatModels.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.Ordinal));
        if (model is null || string.IsNullOrWhiteSpace(model.ModelId)
            || !Uri.TryCreate(model.BaseUrl.Trim(), UriKind.Absolute, out _)) return null;
        return model;
    }

    private bool HasUsableModel(string? modelKey) => ResolveModel(modelKey) is not null;

    /// <summary>子会话标题：优先用显式名称，其次用一句话简述，最后截取提示词第一行。</summary>
    private static string ResolveName(SubagentRequest request)
    {
        var candidate = !string.IsNullOrWhiteSpace(request.Name)
            ? request.Name
            : !string.IsNullOrWhiteSpace(request.Description)
                ? request.Description
                : request.Prompt;

        var line = candidate.ReplaceLineEndings(" ").Trim();
        if (line.Length == 0) return "子代理";
        return line.Length <= NameLength ? line : line[..NameLength] + "…";
    }
}
