// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Serilog;
using SoufflePet.Models;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace SoufflePet.Services;

/// <summary>流式回复中的一个事件。</summary>
public abstract record LlmStreamEvent;

/// <summary>一段助手文本增量。</summary>
public sealed record LlmTextDelta(string Text) : LlmStreamEvent;

/// <summary>模型发起的一次工具调用（已执行，附带参数与结果）。</summary>
public sealed record LlmToolCallEvent(string Name, string Arguments, string Result) : LlmStreamEvent;

/// <summary>LLM 对话服务：通过 Microsoft.Extensions.AI 的 IChatClient 流式请求回复。</summary>
public interface ILlmChatService
{
    /// <summary>
    /// 以流式方式拉取助手回复：文本增量边到边产出，工具调用在执行后单独产出事件
    /// （调用方可用于界面展示）。请求会自动注入系统提示词。
    /// 接口/网络错误以异常抛出，取消通过 cancellationToken 传递。
    /// </summary>
    IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        ChatModelConfig model,
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken);
}

/// <summary>
/// 基于 Microsoft.Extensions.AI（OpenAI 兼容实现）的流式聊天客户端：
/// 负责系统提示词注入、工具调用循环与错误提示，调用方只消费文本增量。
/// </summary>
public sealed class LlmChatService : ILlmChatService
{
    private static readonly ILogger Logger = Log.ForContext<LlmChatService>();

    /// <summary>工具参数/结果的序列化选项：保留中文原样，不转成 \\uXXXX。</summary>
    private static readonly JsonSerializerOptions ToolJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly SouffleSettings _settings;
    private readonly ChatMemory _memory;
    private readonly IReadOnlyList<AITool> _tools;
    private readonly Dictionary<string, AIFunction> _functions;

    public LlmChatService(SouffleSettings settings, ChatTools tools, ChatMemory memory)
    {
        _settings = settings;
        _memory = memory;
        _tools = tools.Tools;
        _functions = tools.Tools.OfType<AIFunction>().ToDictionary(f => f.Name, StringComparer.Ordinal);
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        ChatModelConfig model,
        IReadOnlyList<ChatMessage> history,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Logger.Debug("LLM request {Endpoint} model={Model}", BuildEndpoint(model.BaseUrl), model.ModelId);

        var chars = 0;
        var events = StreamCoreAsync(model, history, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                LlmStreamEvent current;
                try
                {
                    if (!await events.MoveNextAsync().ConfigureAwait(false)) break;
                    current = events.Current;
                }
                catch (OperationCanceledException)
                {
                    Logger.Debug("LLM request cancelled model={Model} chars={Length}", model.ModelId, chars);
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "LLM request failed model={Model}", model.ModelId);
                    throw;
                }

                if (current is LlmTextDelta delta) chars += delta.Text.Length;
                yield return current;
            }

            Logger.Debug("LLM reply finished model={Model} chars={Length}", model.ModelId, chars);
        }
        finally
        {
            await events.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async IAsyncEnumerable<LlmStreamEvent> StreamCoreAsync(
        ChatModelConfig model,
        IReadOnlyList<ChatMessage> history,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var endpoint = BuildEndpoint(model.BaseUrl);
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint),
            // 流式回复可能持续很久，放宽默认的 100 秒网络超时
            NetworkTimeout = TimeSpan.FromMinutes(10),
        };
        var openAiClient = new ChatClient(
            model.ModelId,
            new ApiKeyCredential(NormalizeApiKey(model.ApiKey)),
            options);
        using var chatClient = openAiClient.AsIChatClient();

        // 系统提示词只作为本次请求的首条消息，不进入界面历史
        var messages = new List<ChatMessage>(history.Count + 2);
        if (_settings.ChatSystemPrompt.Trim() is { Length: > 0 } systemPrompt)
            messages.Add(new ChatMessage(ChatRole.System, systemPrompt));

        // 长期记忆：用户档案 + 与本次提问相关的旧对话片段（关闭记忆时为空串）
        var memoryBlock = _memory.BuildMemoryBlock(history.LastOrDefault(m => m.Role == ChatRole.User)?.Text);
        if (memoryBlock.Length > 0)
            messages.Add(new ChatMessage(ChatRole.System, memoryBlock));

        messages.AddRange(history);

        var chatOptions = new ChatOptions
        {
            ModelId = model.ModelId,
            Tools = [.. _tools],
        };

        for (var round = 0; ; round++)
        {
            // 文本增量即时产出，同时留存完整流以便汇总工具调用
            var updates = new List<ChatResponseUpdate>();
            await using (var enumerator = chatClient
                             .GetStreamingResponseAsync(messages, chatOptions, cancellationToken)
                             .GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    ChatResponseUpdate update;
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
                        update = enumerator.Current;
                    }
                    catch (ClientResultException ex)
                    {
                        throw new InvalidOperationException($"接口返回 {ex.Status}：{Truncate(ex.Message, 300)}", ex);
                    }

                    updates.Add(update);
                    if (update.Text is { Length: > 0 } text) yield return new LlmTextDelta(text);
                }
            }

            if (updates.Count == 0) yield break;

            var response = updates.ToChatResponse();
            var calls = response.Messages
                .SelectMany(message => message.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            if (calls.Count == 0) yield break;
            if (round >= _settings.MaxToolRounds)
            {
                Logger.Warning("Tool calls exceeded {MaxRounds} rounds, stop requesting", _settings.MaxToolRounds);
                yield break;
            }

            // 把模型的工具调用消息与本地执行结果接回历史，再请求下一轮
            messages.AddMessages(response);
            foreach (var call in calls)
            {
                var result = await InvokeToolAsync(call, cancellationToken).ConfigureAwait(false);
                messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, result)]));
                yield return new LlmToolCallEvent(call.Name, FormatArguments(call.Arguments), FormatResult(result));
            }
        }
    }

    private async Task<object?> InvokeToolAsync(FunctionCallContent call, CancellationToken cancellationToken)
    {
        if (!_functions.TryGetValue(call.Name, out var function))
        {
            Logger.Warning("Unknown tool {Tool}", call.Name);
            return $"未知工具：{call.Name}";
        }

        try
        {
            var arguments = call.Arguments is { } values ? new AIFunctionArguments(values) : new AIFunctionArguments();
            var result = await function.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            Logger.Debug("Tool {Tool} invoked", call.Name);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "Tool {Tool} failed", call.Name);
            return $"工具执行失败：{ex.Message}";
        }
    }

    /// <summary>工具参数序列化为界面可读的 JSON；无参数时返回空串。</summary>
    private static string FormatArguments(IDictionary<string, object?>? arguments) =>
        arguments is { Count: > 0 } ? JsonSerializer.Serialize(arguments, ToolJsonOptions) : string.Empty;

    /// <summary>工具结果转成界面展示文本（字符串原样返回，其余序列化为 JSON）。</summary>
    private static string FormatResult(object? result) => result switch
    {
        null => string.Empty,
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
        _ => JsonSerializer.Serialize(result, ToolJsonOptions),
    };

    /// <summary>BaseUrl 视为 base 地址；如果用户填了完整接口路径，去掉 /chat/completions 再交给 SDK。</summary>
    private static string BuildEndpoint(string baseUrl)
    {
        var url = baseUrl.Trim().TrimEnd('/');
        const string suffix = "/chat/completions";
        return url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? url[..^suffix.Length].TrimEnd('/')
            : url;
    }

    /// <summary>SDK 不接受空密钥；本地/无鉴权的兼容服务照常发送一个占位值。</summary>
    private static string NormalizeApiKey(string apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) ? "not-required" : apiKey.Trim();

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
