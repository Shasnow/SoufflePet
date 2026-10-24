// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.ClientModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Serilog;
using SoufflePet.Models;
using SoufflePet.Plugin.Sdk;
using SoufflePet.Services.Plugins;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

#pragma warning disable SCME0001 // JsonPatch 目前仍是 OpenAI SDK 的实验性 API

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
public sealed class LlmChatService(
    SouffleSettings settings,
    ChatTools chatTools,
    ChatMemory memory,
    ISkillService skills,
    IPluginService plugins)
    : ILlmChatService
{
    private static readonly ILogger Logger = Log.ForContext<LlmChatService>();

    /// <summary>工具参数/结果的序列化选项：保留中文原样，不转成 \\uXXXX。</summary>
    private static readonly JsonSerializerOptions ToolJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>LLM 请求/响应存档的序列化选项：缩进、中文原样、省略空字段，方便直接阅读。</summary>
    private static readonly JsonSerializerOptions TraceJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// 单次请求的提示词预算（按字符粗估）：中文约 1 字≈1 token，留出余量避免撞上上游硬上限。
    /// 图片等 DataContent 不计入；超出时依次丢弃最旧轮次、截断超长工具结果。
    /// </summary>
    private const int PromptCharBudget = 600_000;

    /// <summary>预算仍超限时，单条工具结果保留的最大字符数。</summary>
    private const int ToolResultCharCap = 8_000;

    /// <summary>工作目录下的项目说明文件名：每轮对话完整注入系统提示词。</summary>
    private const string AgentsFileName = "AGENTS.md";

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
        var messages = new List<ChatMessage>(history.Count + 3);
        if (settings.ChatSystemPrompt.Trim() is { Length: > 0 } systemPrompt)
            messages.Add(new ChatMessage(ChatRole.System, systemPrompt));

        // 工作目录下的 AGENTS.md：每次对话重新读取完整内容，改动后下一轮立即生效
        var agentsBlock = ReadAgentsDocument();
        if (agentsBlock.Length > 0)
            messages.Add(new ChatMessage(ChatRole.System, agentsBlock));

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
        var memoryBlock = memory.BuildMemoryBlock(
            history.LastOrDefault(m => m.Role == ChatRole.User)?.Text,
            historyTexts,
            historyCallIds);
        if (memoryBlock.Length > 0)
            messages.Add(new ChatMessage(ChatRole.System, memoryBlock));

        // 技能清单：每轮注入已启用技能的 frontmatter（名称+描述），正文由模型按需调用 read_skill 读取
        var skillBlock = skills.BuildCatalog();
        if (skillBlock.Length > 0)
            messages.Add(new ChatMessage(ChatRole.System, skillBlock));

        var historyStart = messages.Count;
        messages.AddRange(history);
        var historyEnd = messages.Count;

        // 本地工具 + MCP 工具：MCP 服务器在首次对话时连接，失败只记日志、不影响本地工具
        var tools = await chatTools.GetToolsAsync(cancellationToken).ConfigureAwait(false);
        var functions = new Dictionary<string, AIFunction>(StringComparer.Ordinal);
        foreach (var function in tools.OfType<AIFunction>()) functions.TryAdd(function.Name, function);

        // 工具声明本身也占提示词预算，先扣掉再按剩余额度裁剪对话历史
        var messageBudget = Math.Max(200_000, PromptCharBudget - EstimateToolChars(tools));
        TrimToBudget(messages, historyStart, ref historyEnd, messageBudget);

        // 发送前把请求摘要写入运行日志，便于排查提示词/记忆/技能/历史是否符合预期；
        // 完整内容另存 JSON（固定文件，每次对话覆盖，只保留最新一次）。
        LogRequest(endpoint, model, messages, tools);
        var trace = new LlmTraceDocument
        {
            Timestamp = DateTimeOffset.Now,
            Endpoint = endpoint,
            Model = model.ModelId
        };
        var tracePath = BuildTracePath();

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

            // OpenAI 适配器不会序列化 TextReasoningContent，这里把思考内容补成 reasoning_content
            AttachReasoning(messages);

            // 每轮请求先落盘：即使本次请求失败，也能看到实际发出去的内容
            var roundTrace = new LlmTraceRound { Round = round, Request = CaptureRequest(messages, tools) };
            trace.Rounds.Add(roundTrace);
            WriteTrace(tracePath, trace);

            // 文本增量即时产出，同时留存完整流以便汇总工具调用。
            // 插件响应钩子每轮重新开始，状态不跨轮；处理失败由管线原样放行。
            var pipeline = plugins.CreateResponsePipeline(model.ModelId);
            var updates = new List<ChatResponseUpdate>();
            // 上游可能把回复拆成大量碎片甚至空分片：这里自行合并成一条助手消息，
            // 正文只保留响应钩子输出的可见文本（DSML 等已在插件里剔除），思考原样保留。
            var replyText = new StringBuilder();
            var replyReasoning = new StringBuilder();
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
                        {
                            replyReasoning.Append(reasoning);
                            yield return new LlmReasoningDelta(reasoning);
                        }
                    }

                    if (update.Text is { Length: > 0 } text)
                    {
                        var visible = pipeline.Process(text);
                        if (visible.Length > 0)
                        {
                            replyText.Append(visible);
                            yield return new LlmTextDelta(visible);
                        }
                    }
                }
            }

            var tail = pipeline.Flush();
            if (tail.Length > 0)
            {
                replyText.Append(tail);
                yield return new LlmTextDelta(tail);
            }

            if (updates.Count == 0) break;

            var response = updates.ToChatResponse();
            roundTrace.Response = CaptureResponse(response);
            WriteTrace(tracePath, trace);

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

            // 只追加一条合并后的助手消息：上游碎片与空消息不进历史，正文已过响应钩子
            var assistantMessage = BuildAssistantMessage(replyText, replyReasoning, calls);

            // 达到最大轮次：新调用不再执行，但每次都要作为工具结果回给模型，
            // 由模型自行决定基于已有信息作答还是继续请求（继续请求同样只会拿到这条提示）。
            if (round >= settings.MaxToolRounds)
            {
                Logger.Warning("Tool calls exceeded {MaxRounds} rounds, reject further calls", settings.MaxToolRounds);

                messages.Add(assistantMessage);
                foreach (var call in calls)
                {
                    var arguments = FormatArguments(call.Arguments);
                    var limitText = $"未执行：本次对话已达到最大工具调用轮次（{settings.MaxToolRounds} 轮）。";

                    yield return new LlmToolCallStartedEvent(call.CallId, call.Name, arguments);
                    messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, limitText)]));
                    yield return new LlmToolCallEvent(call.CallId, call.Name, arguments, limitText);
                }

                continue;
            }

            // 把模型的工具调用消息与本地执行结果接回历史，再请求下一轮
            messages.Add(assistantMessage);
            foreach (var call in calls)
            {
                var arguments = FormatArguments(call.Arguments);
                // 先通知界面「开始调用」，长耗时工具（MCP、命令执行）也能立刻看到反馈
                yield return new LlmToolCallStartedEvent(call.CallId, call.Name, arguments);

                var result = await InvokeToolAsync(call, functions, cancellationToken).ConfigureAwait(false);

                // 工具返回 AIContent 集合（截图、read_file 的图片/音频、MCP 返回的媒体等）：
                // 文本作为工具结果，媒体内容作为独立用户消息附加，让支持多模态的模型真正看到画面、听到声音。
                if (result is IEnumerable<AIContent> contents)
                {
                    var list = contents as IList<AIContent> ?? [.. contents];
                    var text = ExtractText(list);
                    var media = list.OfType<DataContent>().ToList();
                    if (media.Count > 0)
                    {
                        var summary = text.Length > 0 ? text : "工具返回了媒体内容。";
                        messages.Add(new ChatMessage(ChatRole.Tool,
                            [new FunctionResultContent(call.CallId, summary)]));
                        messages.Add(new ChatMessage(ChatRole.User, [.. media]));
                        yield return new LlmToolCallEvent(call.CallId, call.Name, arguments, summary);
                        continue;
                    }

                    // 纯文本内容集合：拼成字符串回传，避免被序列化成 JSON
                    if (text.Length > 0) result = text;
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
    /// 再截断超长工具结果；图片等 DataContent 不参与预算计算，始终保留。
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

        // 2. 单条工具结果过大时截断，尽量保留对话结构
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

    /// <summary>估算单条消息的字符数：正文 + 工具参数/结果；图片等 DataContent 不计入预算。</summary>
    private static int EstimateChars(ChatMessage message)
    {
        var chars = message.Text.Length;
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case FunctionCallContent call:
                    chars += FormatArguments(call.Arguments).Length;
                    break;
                case FunctionResultContent result:
                    chars += ResultChars(result.Result);
                    break;
                case TextReasoningContent reasoning:
                    chars += reasoning.Text.Length;
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
    /// 把工具结果规整成统一形态：单个内容块包装成集合；AIFunctionFactory 默认把返回值序列化成
    /// JsonElement，字符串先还原成纯文本；MCP 工具返回的 JsonElement（序列化的 CallToolResult）
    /// 解析成 AIContent 集合，其余类型原样返回。
    /// </summary>
    private static object? NormalizeToolResult(object? result) => result switch
    {
        null => null,
        AIContent content => new[] { content },
        IEnumerable<AIContent> contents => contents as IList<AIContent> ?? [.. contents],
        // 字符串不还原时，OpenAI 适配器按 JSON 原文写入 tool 消息，模型看到的是带引号、换行被转义的字符串
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
        JsonElement element => ResolveMcpJsonResult(element) ?? result,
        _ => result
    };

    /// <summary>解析 MCP 工具结果；不是 CallToolResult 结构时返回 null（调用方保持原样）。</summary>
    private static IReadOnlyList<AIContent>? ResolveMcpJsonResult(JsonElement element)
    {
        if (ConvertMcpResult(element) is not { } contents) return null;
        if (contents.Count > 0) return contents;

        // 结构匹配但没解析出可用内容（例如损坏的 base64）：给占位文本，绝不能把原始 JSON 塞进提示词
        return [new TextContent("工具返回的内容无法解析。")];
    }

    /// <summary>
    /// 解析 MCP 工具结果：InvokeAsync 返回的是序列化的 CallToolResult，
    /// 形如 {"content":[{"type":"text","text":"…"},{"type":"image","data":"base64","mimeType":"image/png"}]}。
    /// 不解析就会被当普通文本塞进提示词——模型看不到图，还白烧几十万字符的 base64 token。
    /// 不是这种结构时返回 null，调用方保持原样。
    /// </summary>
    private static IReadOnlyList<AIContent>? ConvertMcpResult(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty("content", out var blocks) || blocks.ValueKind != JsonValueKind.Array) return null;

        var contents = new List<AIContent>(blocks.GetArrayLength());
        foreach (var block in blocks.EnumerateArray()) AppendMcpContent(block, contents);
        return contents;
    }

    private static void AppendMcpContent(JsonElement block, List<AIContent> contents)
    {
        if (block.ValueKind != JsonValueKind.Object) return;

        var type = GetString(block, "type");
        switch (type)
        {
            case "text":
                if (GetString(block, "text") is { } text) contents.Add(new TextContent(text));
                break;

            case "image":
            case "audio":
                if (DecodeBase64(GetString(block, "data")) is { } data)
                {
                    var mediaType = GetString(block, "mimeType")
                        ?? (type == "image" ? "image/png" : "audio/wav");
                    contents.Add(new DataContent(data, mediaType));
                }
                break;

            case "resource":
                if (block.TryGetProperty("resource", out var resource) && resource.ValueKind == JsonValueKind.Object)
                    AppendMcpResource(resource, contents);
                break;

            case "resource_link":
                var uri = GetString(block, "uri");
                var name = GetString(block, "name") ?? uri;
                if (name is not null) contents.Add(new TextContent($"资源链接：{name}（{uri}）"));
                break;
        }
    }

    /// <summary>内嵌资源：text 直接作为文本；blob 仅还原图片/音频，其余类型只提示链接，避免 base64 撑爆提示词。</summary>
    private static void AppendMcpResource(JsonElement resource, List<AIContent> contents)
    {
        if (GetString(resource, "text") is { } text)
        {
            contents.Add(new TextContent(text));
            return;
        }

        var mediaType = GetString(resource, "mimeType");
        var uri = GetString(resource, "uri");
        var blob = GetString(resource, "blob");

        if (blob is not null && mediaType is not null
            && (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            && DecodeBase64(blob) is { } data)
        {
            contents.Add(new DataContent(data, mediaType));
            return;
        }

        if (uri is not null) contents.Add(new TextContent($"资源：{uri}（{mediaType ?? "未知类型"}，内容未展开）"));
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static byte[]? DecodeBase64(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>把内容集合里的文本块拼成多行文本。</summary>
    private static string ExtractText(IEnumerable<AIContent> contents) =>
        string.Join("\n", contents.OfType<TextContent>()
            .Select(content => content.Text)
            .Where(line => !string.IsNullOrWhiteSpace(line)));

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

    /// <summary>
    /// 把本轮流式增量合并成一条助手消息：思考在前、正文居中、工具调用在最后。
    /// 上游可能把回复拆成大量碎片甚至空分片，直接 AddMessages(response) 会让历史迅速膨胀。
    /// </summary>
    private static ChatMessage BuildAssistantMessage(
        StringBuilder text,
        StringBuilder reasoning,
        List<FunctionCallContent> calls)
    {
        var contents = new List<AIContent>(calls.Count + 2);
        if (reasoning.Length > 0) contents.Add(new TextReasoningContent(reasoning.ToString()));
        if (text.Length > 0) contents.Add(new TextContent(text.ToString()));
        contents.AddRange(calls);
        return new ChatMessage(ChatRole.Assistant, contents);
    }

    /// <summary>
    /// 把助手消息里的思考内容补回请求体：OpenAI 适配器序列化时会忽略 TextReasoningContent，
    /// 而 DeepSeek 等推理模型要求工具循环中回传 reasoning_content。
    /// 做法是构造 SDK 的原始助手消息并打 JsonPatch，适配器会优先使用 RawRepresentation。
    /// </summary>
    private static void AttachReasoning(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message.Role != ChatRole.Assistant) continue;

            var reasoning = string.Concat(message.Contents
                .OfType<TextReasoningContent>()
                .Select(content => content.Text));
            if (reasoning.Length == 0) continue;

            var calls = message.Contents.OfType<FunctionCallContent>().ToList();
            AssistantChatMessage raw;
            if (calls.Count > 0)
            {
                raw = new AssistantChatMessage(calls.Select(call =>
                {
                    var arguments = FormatArguments(call.Arguments);
                    return ChatToolCall.CreateFunctionToolCall(
                        call.CallId,
                        call.Name,
                        BinaryData.FromString(arguments.Length > 0 ? arguments : "{}"));
                }));

                if (message.Text.Length > 0)
                    raw.Patch.Set("$.content"u8,
                        BinaryData.FromString(JsonSerializer.Serialize(message.Text, ToolJsonOptions)));
            }
            else
            {
                raw = new AssistantChatMessage(message.Text);
            }

            raw.Patch.Set("$.reasoning_content"u8,
                BinaryData.FromString(JsonSerializer.Serialize(reasoning, ToolJsonOptions)));
            message.RawRepresentation = raw;
        }
    }

    /// <summary>请求摘要：完整内容见最新一次对话的 JSON 存档（logs/llm/latest.json）。</summary>
    private static void LogRequest(
        string endpoint,
        ChatModelConfig model,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AITool> tools) =>
        Logger.Debug("LLM request {Endpoint} model={Model} messages={Messages} tools={Tools}",
            endpoint, model.ModelId, messages.Count, tools.Count);

    /// <summary>存档路径固定为 logs/llm/latest.json：每次对话覆盖写入，目录不会无限增长。</summary>
    private static string BuildTracePath()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "logs", "llm");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "latest.json");
    }

    /// <summary>整份存档覆盖写入：每轮追加后重写，失败只记日志，不影响对话。</summary>
    private static void WriteTrace(string path, LlmTraceDocument trace)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(trace, TraceJsonOptions));
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "write LLM trace {Path} failed", path);
        }
    }

    private static LlmTraceRequest CaptureRequest(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AITool> tools) => new()
        {
            Messages = [.. messages.Select(CaptureMessage)],
            Tools = [.. tools.Select(tool => new LlmTraceTool
            {
                Name = tool.Name,
                Description = tool.Description,
                Schema = tool is AIFunction function ? function.JsonSchema.Clone() : null
            })]
        };

    private static LlmTraceResponse CaptureResponse(ChatResponse response) => new()
    {
        Messages = [.. response.Messages.Select(CaptureMessage)],
        FinishReason = response.FinishReason?.ToString(),
        Usage = response.Usage is { } usage
            ? new LlmTraceUsage
            {
                InputTokens = usage.InputTokenCount ?? 0,
                OutputTokens = usage.OutputTokenCount ?? 0,
                CachedInputTokens = usage.CachedInputTokenCount ?? 0
            }
            : null
    };

    /// <summary>展开单条消息；媒体只记录类型、大小与链接，避免 base64 把存档撑爆。</summary>
    private static LlmTraceMessage CaptureMessage(ChatMessage message) => new()
    {
        Role = message.Role.Value,
        Contents = [.. message.Contents.Select(CaptureContent)]
    };

    private static LlmTraceContent CaptureContent(AIContent content) => content switch
    {
        TextContent text => new LlmTraceContent { Type = "text", Text = text.Text },
        TextReasoningContent reasoning => new LlmTraceContent { Type = "reasoning", Text = reasoning.Text },
        FunctionCallContent call => new LlmTraceContent
        {
            Type = "toolCall",
            Name = call.Name,
            Arguments = call.Arguments is null
                ? null
                : JsonSerializer.SerializeToElement(call.Arguments, ToolJsonOptions)
        },
        FunctionResultContent result => new LlmTraceContent
        {
            Type = "toolResult",
            Result = result.Result is null
                ? null
                : JsonSerializer.SerializeToElement(result.Result, ToolJsonOptions)
        },
        DataContent data => new LlmTraceContent
        {
            Type = "media",
            MediaType = data.MediaType,
            Bytes = data.Data.Length
        },
        UriContent uri => new LlmTraceContent { Type = "mediaLink", Uri = uri.Uri.ToString() },
        ErrorContent error => new LlmTraceContent { Type = "error", Error = error.Message },
        _ => new LlmTraceContent { Type = content.GetType().Name }
    };

    /// <summary>一次对话（从用户提问到最终回复）的完整 LLM 往返存档，工具循环的每轮都在 Rounds 里。</summary>
    private sealed class LlmTraceDocument
    {
        public DateTimeOffset Timestamp { get; init; }
        public string Endpoint { get; init; } = string.Empty;
        public string Model { get; init; } = string.Empty;
        public List<LlmTraceRound> Rounds { get; } = [];
    }

    private sealed class LlmTraceRound
    {
        public int Round { get; init; }
        public LlmTraceRequest Request { get; init; } = new();
        public LlmTraceResponse? Response { get; set; }
    }

    private sealed class LlmTraceRequest
    {
        public List<LlmTraceMessage> Messages { get; init; } = [];
        public List<LlmTraceTool> Tools { get; init; } = [];
    }

    private sealed class LlmTraceResponse
    {
        public List<LlmTraceMessage> Messages { get; init; } = [];
        public string? FinishReason { get; init; }
        public LlmTraceUsage? Usage { get; init; }
    }

    private sealed class LlmTraceUsage
    {
        public long InputTokens { get; init; }
        public long OutputTokens { get; init; }
        public long CachedInputTokens { get; init; }
    }

    private sealed class LlmTraceTool
    {
        public string Name { get; init; } = string.Empty;
        public string? Description { get; init; }
        public JsonElement? Schema { get; init; }
    }

    private sealed class LlmTraceMessage
    {
        public string Role { get; init; } = string.Empty;
        public List<LlmTraceContent> Contents { get; init; } = [];
    }

    /// <summary>消息内容：按类型只填相关字段，其余为 null 不落盘。</summary>
    private sealed class LlmTraceContent
    {
        public string Type { get; init; } = string.Empty;
        public string? Text { get; init; }
        public string? Name { get; init; }
        public JsonElement? Arguments { get; init; }
        public JsonElement? Result { get; init; }
        public string? MediaType { get; init; }
        public long? Bytes { get; init; }
        public string? Uri { get; init; }
        public string? Error { get; init; }
    }

    /// <summary>
    /// 读取工作目录下 AGENTS.md 的完整内容，包成系统提示块；不缓存，每次对话重新读盘，
    /// 文件不存在、内容为空或读取失败时返回空串（失败只记日志，不影响本次对话）。
    /// </summary>
    private static string ReadAgentsDocument()
    {
        var path = Path.Combine(Environment.CurrentDirectory, AgentsFileName);

        try
        {
            if (!File.Exists(path)) return string.Empty;

            var content = File.ReadAllText(path);
            return content.Trim().Length == 0
                ? string.Empty
                : $"以下是工作目录下 {AgentsFileName} 的内容，是本项目的约定与补充说明，请照此执行：\n\n{content}";
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "read {File} {Path} failed", AgentsFileName, path);
            return string.Empty;
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