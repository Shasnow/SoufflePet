// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
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

/// <summary>一段思考过程增量（推理模型返回；只用于界面展示，不进入请求历史与长期记忆）。</summary>
public sealed record LlmReasoningDelta(string Text) : LlmStreamEvent;

/// <summary>模型发起一次工具调用：在真正执行前先产出，界面据此立即显示，避免耗时工具看起来没反应。</summary>
public sealed record LlmToolCallStartedEvent(string CallId, string Name, string Arguments) : LlmStreamEvent;

/// <summary>一次工具调用已执行结束（CallId 与 Started 事件对应），附带参数与结果。</summary>
public sealed record LlmToolCallEvent(string CallId, string Name, string Arguments, string Result) : LlmStreamEvent;

/// <summary>本次回复的 token 用量（含多轮工具调用的总和），在流结束后产出。</summary>
public sealed record LlmUsageEvent(long InputTokens, long OutputTokens, long CachedInputTokens) : LlmStreamEvent;

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
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 单次请求的提示词预算（按字符粗估）：中文约 1 字≈1 token，base64 图片按字符计，
    /// 留出余量避免撞上上游 1M 的硬上限。超出时依次丢弃最旧轮次、省略图片、截断超长工具结果。
    /// </summary>
    private const int PromptCharBudget = 600_000;

    /// <summary>预算仍超限时，单条工具结果保留的最大字符数。</summary>
    private const int ToolResultCharCap = 8_000;

    /// <summary>图片因预算被省略时替换成的文本。</summary>
    private const string ImageOmittedText = "（图片已省略：超出上下文预算，可缩小窗口或重开对话后再试）";

    private readonly SouffleSettings _settings;
    private readonly ChatMemory _memory;
    private readonly ChatTools _chatTools;
    private readonly ISkillService _skills;

    public LlmChatService(SouffleSettings settings, ChatTools tools, ChatMemory memory, ISkillService skills)
    {
        _settings = settings;
        _memory = memory;
        _chatTools = tools;
        _skills = skills;
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        ChatModelConfig model,
        IReadOnlyList<ChatMessage> history,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
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
            NetworkTimeout = TimeSpan.FromMinutes(10)
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

        // 长期记忆：用户档案 + 与本次提问相关的旧对话片段（关闭记忆时为空串）。
        // 本次请求已带上的历史对话要排除，否则刚发的消息会作为「相关回忆」重复注入；
        // 工具消息的结构化内容不在 Text 里，需要额外按 CallId 排除。
        var historyTexts = history
            .Select(m => m.Text.Trim())
            .Where(t => t.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var historyCallIds = history
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Select(c => c.CallId)
            .ToHashSet(StringComparer.Ordinal);
        var memoryBlock = _memory.BuildMemoryBlock(
            history.LastOrDefault(m => m.Role == ChatRole.User)?.Text,
            historyTexts,
            historyCallIds);
        if (memoryBlock.Length > 0)
            messages.Add(new ChatMessage(ChatRole.System, memoryBlock));

        // 技能清单：每轮注入已启用技能的 frontmatter（名称+描述），正文由模型按需调用 read_skill 读取
        var skillBlock = _skills.BuildCatalog();
        if (skillBlock.Length > 0)
            messages.Add(new ChatMessage(ChatRole.System, skillBlock));

        var historyStart = messages.Count;
        messages.AddRange(history);
        var historyEnd = messages.Count;

        // 本地工具 + MCP 工具：MCP 服务器在首次对话时连接，失败只记日志、不影响本地工具
        var tools = await _chatTools.GetToolsAsync(cancellationToken).ConfigureAwait(false);
        var functions = new Dictionary<string, AIFunction>(StringComparer.Ordinal);
        foreach (var function in tools.OfType<AIFunction>()) functions.TryAdd(function.Name, function);

        // 工具声明本身也占提示词预算，先扣掉再按剩余额度裁剪对话历史
        var messageBudget = Math.Max(200_000, PromptCharBudget - EstimateToolChars(tools));
        TrimToBudget(messages, historyStart, ref historyEnd, messageBudget);

        // 发送前把完整请求写入调试日志，便于排查提示词/记忆/技能/历史是否符合预期
        LogRequest(endpoint, model, messages, tools);

        var chatOptions = new ChatOptions
        {
            ModelId = model.ModelId,
            Tools = [.. tools]
        };

        // 带工具调用时一轮回复会发出多次请求，每次都单独计费：逐轮累加，最后统一上报
        var inputTokens = 0L;
        var outputTokens = 0L;
        var cachedInputTokens = 0L;

        for (var round = 0;; round++)
        {
            // 工具结果会不断追加，每轮请求前重新检查预算，防止长工具链把提示词撑爆
            TrimToBudget(messages, historyStart, ref historyEnd, messageBudget);

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
                    // 思考内容与正文分开产出，界面上各自展示
                    foreach (var content in update.Contents)
                    {
                        if (content is TextReasoningContent { Text: { Length: > 0 } reasoning })
                            yield return new LlmReasoningDelta(reasoning);
                    }

                    if (update.Text is { Length: > 0 } text) yield return new LlmTextDelta(text);
                }
            }

            if (updates.Count == 0) break;

            var response = updates.ToChatResponse();
            if (response.Usage is { } usage)
            {
                inputTokens += usage.InputTokenCount ?? 0;
                outputTokens += usage.OutputTokenCount ?? 0;
                cachedInputTokens += usage.CachedInputTokenCount ?? 0;
            }

            var calls = response.Messages
                .SelectMany(message => message.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            if (calls.Count == 0) break;
            if (round >= _settings.MaxToolRounds)
            {
                Logger.Warning("Tool calls exceeded {MaxRounds} rounds, stop requesting", _settings.MaxToolRounds);
                break;
            }

            // 把模型的工具调用消息与本地执行结果接回历史，再请求下一轮
            messages.AddMessages(response);
            foreach (var call in calls)
            {
                var arguments = FormatArguments(call.Arguments);
                // 先通知界面「开始调用」，长耗时工具（MCP、命令执行）也能立刻看到反馈
                yield return new LlmToolCallStartedEvent(call.CallId, call.Name, arguments);

                var result = await InvokeToolAsync(call, functions, cancellationToken).ConfigureAwait(false);

                // 带图片的工具（如截图）：返回值会被序列化成 JSON，类型判断识别不出图片，
                // 改为主动取走 ChatTools 暂存的图片；工具结果只放文本，
                // 图片作为独立用户消息附加，让支持视觉输入的模型能真正看到画面。
                var pendingImage = _chatTools.TakePendingImage();
                if (pendingImage is not null)
                {
                    messages.Add(new ChatMessage(ChatRole.Tool,
                        [new FunctionResultContent(call.CallId, pendingImage.Text)]));
                    messages.Add(new ChatMessage(ChatRole.User,
                    [
                        new TextContent("这是刚才工具返回的画面，请结合图片内容回答。"),
                        new DataContent(pendingImage.Data, pendingImage.MediaType)
                    ]));
                    yield return new LlmToolCallEvent(call.CallId, call.Name, arguments, pendingImage.Text);
                    continue;
                }

                // MCP 等其它工具直接返回图片内容的情形（已在 InvokeToolAsync 里归一化）
                if (result is ToolImageResult image)
                {
                    messages.Add(new ChatMessage(ChatRole.Tool,
                        [new FunctionResultContent(call.CallId, image.Text)]));
                    messages.Add(new ChatMessage(ChatRole.User,
                    [new DataContent(image.Data, image.MediaType)]));
                    yield return new LlmToolCallEvent(call.CallId, call.Name, arguments, image.Text);
                    continue;
                }

                messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, result)]));
                yield return new LlmToolCallEvent(call.CallId, call.Name, arguments, FormatResult(result));
            }
        }

        // 用量整体产出一个事件：界面据此在助手气泡下方与底部统计里展示
        if (inputTokens > 0 || outputTokens > 0)
            yield return new LlmUsageEvent(inputTokens, outputTokens, cachedInputTokens);
    }

    /// <summary>
    /// 按字符预算裁剪本次请求：先整轮丢弃最旧对话（系统消息与当前提问始终保留），
    /// 再把图片替换成占位文本，最后截断超长工具结果。
    /// </summary>
    private static void TrimToBudget(List<ChatMessage> messages, int historyStart, ref int historyEnd, int budget)
    {
        var total = messages.Sum(EstimateChars);
        if (total <= budget) return;

        var before = total;

        // 1. 从最旧的一轮开始整轮删除；最后一轮（当前提问）及其后的工具记录不动
        while (total > budget)
        {
            var firstUser = FindUser(messages, historyStart, historyEnd);
            if (firstUser < 0) break;
            var nextUser = FindUser(messages, firstUser + 1, historyEnd);
            if (nextUser < 0) break;

            for (var i = firstUser; i < nextUser; i++) total -= EstimateChars(messages[i]);
            messages.RemoveRange(firstUser, nextUser - firstUser);
            historyEnd -= nextUser - firstUser;
        }

        // 2. 图片 base64 在只按文本计数的上游约等于 1 token/字符，必要时省略
        if (total > budget) total = OmitImages(messages, total, budget);

        // 3. 单条工具结果过大时截断，尽量保留对话结构
        if (total > budget) total = CapToolResults(messages, total, budget);

        if (total > budget)
            Logger.Warning("prompt over budget after trim chars={Chars} budget={Budget}", total, budget);
        else
            Logger.Debug("prompt trimmed chars {Before} -> {After} budget={Budget}", before, total, budget);
    }

    /// <summary>在 [start, end) 内找用户发言；找不到返回 -1。</summary>
    private static int FindUser(List<ChatMessage> messages, int start, int end)
    {
        for (var i = start; i < end; i++)
            if (messages[i].Role == ChatRole.User) return i;
        return -1;
    }

    /// <summary>逐条把图片内容替换成占位文本，直到回到预算内。</summary>
    private static int OmitImages(List<ChatMessage> messages, int total, int budget)
    {
        for (var i = 0; i < messages.Count && total > budget; i++)
        {
            var message = messages[i];
            if (!message.Contents.OfType<DataContent>().Any()) continue;

            total -= EstimateChars(message);
            messages[i] = new ChatMessage(message.Role,
                message.Contents.Select(c => c is DataContent ? new TextContent(ImageOmittedText) : c).ToList());
            total += EstimateChars(messages[i]);
        }

        return total;
    }

    /// <summary>逐条截断过长的工具结果，直到回到预算内。</summary>
    private static int CapToolResults(List<ChatMessage> messages, int total, int budget)
    {
        for (var i = 0; i < messages.Count && total > budget; i++)
        {
            var message = messages[i];
            if (message.Contents.OfType<FunctionResultContent>().FirstOrDefault() is not { Result: string text } result)
                continue;
            if (text.Length <= ToolResultCharCap) continue;

            var trimmed = text[..ToolResultCharCap] + "\n…（已截断：超出上下文预算）";
            total -= text.Length - trimmed.Length;
            messages[i] = new ChatMessage(message.Role, [new FunctionResultContent(result.CallId, trimmed)]);
        }

        return total;
    }

    /// <summary>估算单条消息的字符数：正文 + base64 图片 + 工具参数/结果。</summary>
    private static int EstimateChars(ChatMessage message)
    {
        var chars = message.Text.Length;
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case DataContent data:
                    chars += data.Data.Length / 3 * 4 + 128; // base64 膨胀 + data URL 前缀
                    break;
                case FunctionCallContent call:
                    chars += FormatArguments(call.Arguments).Length;
                    break;
                case FunctionResultContent result:
                    chars += ResultChars(result.Result);
                    break;
            }
        }

        return chars;
    }

    private static int ResultChars(object? result) => result switch
    {
        null => 0,
        string text => text.Length,
        JsonElement element => element.ValueKind == JsonValueKind.String
            ? element.GetString()?.Length ?? 0
            : element.GetRawText().Length,
        _ => result.ToString()?.Length ?? 0
    };

    /// <summary>估算工具声明（名称 + JSON Schema）占用的字符数。</summary>
    private static int EstimateToolChars(IReadOnlyList<AITool> tools)
    {
        var chars = 0;
        foreach (var tool in tools)
        {
            chars += tool.Name.Length;
            chars += tool is AIFunction function ? function.JsonSchema.GetRawText().Length : 200;
        }

        return chars;
    }

    private async Task<object?> InvokeToolAsync(
        FunctionCallContent call,
        Dictionary<string, AIFunction> functions,
        CancellationToken cancellationToken)
    {
        if (!functions.TryGetValue(call.Name, out var function))
        {
            Logger.Warning("Unknown tool {Tool}", call.Name);
            return $"未知工具：{call.Name}";
        }

        try
        {
            var arguments = call.Arguments is { } values ? new AIFunctionArguments(values) : new AIFunctionArguments();
            var result = await function.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            Logger.Debug("Tool {Tool} invoked", call.Name);
            return NormalizeToolResult(result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 工具自身超时或内部取消：当作普通结果回给模型，不能连带取消本次 LLM 请求
            Logger.Warning("Tool {Tool} cancelled internally", call.Name);
            return $"工具 {call.Name} 执行超时或已中止。";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "Tool {Tool} failed", call.Name);
            return $"工具执行失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 把工具结果规整成统一形态：带图片的结果（如 MCP 截图工具返回的 DataContent）
    /// 转换成 <see cref="ToolImageResult" />，让图片走多模态通道，而不是把 base64 当文本塞进对话。
    /// </summary>
    private static object? NormalizeToolResult(object? result) => result switch
    {
        null => null,
        ToolImageResult => result,
        DataContent data => new ToolImageResult("工具返回了一张图片。", data.Data.ToArray(), data.MediaType),
        // MCP 工具把 CallToolResult 的 Content 列表映射成 AIContent 集合返回
        IEnumerable<AIContent> contents => FromContents(contents),
        _ => result
    };

    /// <summary>从内容集合中抽出第一张图片；没有图片时原样返回，仍按普通文本结果处理。</summary>
    private static object FromContents(IEnumerable<AIContent> contents)
    {
        var list = contents as IList<AIContent> ?? [.. contents];
        if (list.OfType<DataContent>().FirstOrDefault() is not { } image) return list;

        var text = string.Join("\n", list.OfType<TextContent>()
            .Select(content => content.Text)
            .Where(line => !string.IsNullOrWhiteSpace(line)));

        return new ToolImageResult(
            text.Length > 0 ? text : "工具返回了一张图片。",
            image.Data.ToArray(),
            image.MediaType);
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
        _ => JsonSerializer.Serialize(result, ToolJsonOptions)
    };

    /// <summary>发送前把完整请求写入调试日志：逐条打印消息（含系统提示词、记忆、技能清单），再列出本次可调用的工具。</summary>
    private static void LogRequest(
        string endpoint,
        ChatModelConfig model,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AITool> tools)
    {
        var builder = new StringBuilder();
        builder.Append("LLM request ").Append(endpoint)
            .Append(" model=").Append(model.ModelId)
            .Append(" messages=").Append(messages.Count)
            .Append(" tools=").Append(tools.Count);

        foreach (var message in messages)
        {
            builder.Append("\n--- ").Append(message.Role).Append(" ---\n");
            if (message.Text is { Length: > 0 } text)
                builder.Append(text);
            else if (message.Contents.Count > 0)
                builder.Append('[').Append(string.Join("、", message.Contents.Select(DescribeContent))).Append(']');
            else
                builder.Append("(空)");
        }

        if (tools.Count > 0)
        {
            builder.Append("\n工具：");
            foreach (var tool in tools) builder.Append("\n- ").Append(tool.Name);
        }

        Logger.Debug(builder.ToString());
    }

    private static string DescribeContent(AIContent content) => content switch
    {
        FunctionCallContent call => $"调用 {call.Name}",
        FunctionResultContent => "工具结果",
        TextReasoningContent => "思考内容",
        _ => content.GetType().Name
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