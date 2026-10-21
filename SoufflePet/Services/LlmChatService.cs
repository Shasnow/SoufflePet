// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Serilog;
using SoufflePet.Models;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace SoufflePet.Services;

/// <summary>LLM 对话服务：通过 Microsoft.Extensions.AI 的 IChatClient 流式请求回复。</summary>
public interface ILlmChatService
{
    /// <summary>
    /// 以流式方式拉取助手回复，每个元素是一段增量文本（空片段已过滤）。
    /// 接口/网络错误以异常抛出，取消通过 cancellationToken 传递。
    /// </summary>
    IAsyncEnumerable<string> StreamReplyAsync(
        ChatModelConfig model,
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken);
}

/// <summary>
/// 基于 Microsoft.Extensions.AI（OpenAI 兼容实现）的流式聊天客户端。
/// 只负责传输，错误提示由调用方处理。
/// </summary>
public sealed class LlmChatService : ILlmChatService
{
    private static readonly ILogger Logger = Log.ForContext<LlmChatService>();

    public async IAsyncEnumerable<string> StreamReplyAsync(
        ChatModelConfig model,
        IReadOnlyList<ChatMessage> history,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var endpoint = BuildEndpoint(model.BaseUrl);
        Logger.Debug("LLM request {Endpoint} model={Model}", endpoint, model.ModelId);

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

        var chatOptions = new ChatOptions { ModelId = model.ModelId };

        var stream = chatClient.GetStreamingResponseAsync(history, chatOptions, cancellationToken);
        await using var enumerator = stream.GetAsyncEnumerator(cancellationToken);
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

            if (update.Text is { Length: > 0 } text) yield return text;
        }
    }

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
