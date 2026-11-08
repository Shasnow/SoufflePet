// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.ClientModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
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
using SoufflePet.Core.Chat;
using SoufflePet.Models;
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
/// <param name="Label">气泡标题（如「运行命令 · npm test」）；为空时界面回退到工具名。</param>
public sealed record LlmToolCallStartedEvent(string CallId, string Name, string Arguments, string? Label = null)
    : LlmStreamEvent;

/// <summary>一次工具调用已执行结束（CallId 与 Started 事件对应），附带参数与结果。</summary>
/// <param name="Label">气泡标题；与 Started 事件一致。</param>
public sealed record LlmToolCallEvent(string CallId, string Name, string Arguments, string Result, string? Label = null)
    : LlmStreamEvent;

/// <summary>
/// 本次回复为止累计的 token 用量（含多轮工具调用的总和）。每完成一轮就产出一份累计值，
/// 这样中途取消 / 出错时，已完成轮次已经花掉的用量不会因为拿不到「结束事件」而丢失。
/// </summary>
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
    IPluginService plugins,
    PlatformService platforms)
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
        var endpoint = OpenAiEndpoint.BaseUrl(model.BaseUrl);
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint),
            // 流式回复可能持续很久，放宽默认的 100 秒网络超时
            NetworkTimeout = TimeSpan.FromMinutes(10)
        };
        var openAiClient = new ChatClient(
            model.ModelId,
            new ApiKeyCredential(OpenAiEndpoint.ApiKey(model.ApiKey)),
            options);
        using var chatClient = openAiClient.AsIChatClient();

        // 系统提示词只作为本次请求的首条消息，不进入界面历史。
        // 全部系统块合并成一条，避免多段 System 消息分散模型注意力。
        //
        // 缓存友好：系统提示词处在整个请求的最前面，任何一处变化都会让服务端的前缀缓存
        // 从变化处起全部失效。因此各块按「变化频率从低到高」排列——
        // 人设 → 工作区（AGENTS.md）→ 技能清单 → 消息平台 → 用户档案；
        // 随每轮提问变化的「相关回忆」与「当前时间」不写进系统提示词，改由 ChatViewModel
        // 追加到本轮用户消息末尾并随消息进入历史，让「系统提示词 + 历史对话」这段前缀
        // 逐字节稳定，后续提问可以持续命中缓存。调整顺序前请先读这段注释。
        var messages = new List<ChatMessage>(history.Count + 3);
        var systemPrompt = new StringBuilder();
        systemPrompt.AppendLine("## 人设\n");
        systemPrompt.AppendLine(settings.ChatSystemPrompt);

        // 工作区信息：工作目录下的 AGENTS.md：每次对话重新读取完整内容，改动后下一轮立即生效
        BuildWorkspaceBlock(systemPrompt);
        // 技能清单：每轮注入已启用技能的 frontmatter（名称+描述），正文由模型按需调用 read_skill 读取
        skills.BuildCatalog(systemPrompt);
        // 消息平台：只有真的接入了平台才注入使用说明，没有平台时提示词里不出现这类内容
        BuildPlatformBlock(systemPrompt);
        // 长期记忆里的用户档案：只在 remember / forget 后变化，放在系统提示词最末位，尽量少影响前缀
        memory.BuildProfileBlock(systemPrompt);

        var stringSystemPrompt = systemPrompt.ToString();
        // 记录哈希：两次请求的哈希相同，说明系统提示词这段前缀没变、缓存仍有效，
        // 排查「命中率突然掉到 0」时先看这里有没有每轮都变。
        Logger.Debug("System prompt hash={Hash} chars={Length}",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stringSystemPrompt)))[..16],
            stringSystemPrompt.Length);
        await File.WriteAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "logs", "latest_system_prompt.md"), 
            stringSystemPrompt, cancellationToken);
        messages.Add(new ChatMessage(ChatRole.System, stringSystemPrompt));

        // 历史里已经包含本轮用户消息（以及附加的时间 / 相关回忆，由 ChatViewModel 组装并写入历史）：
        // 附加内容随消息进入请求历史，下一轮请求的前缀才能与上一轮逐字节一致。
        messages.AddRange(history);

        // 本地工具 + MCP 工具：MCP 服务器在首次对话时连接，失败只记日志、不影响本地工具
        var tools = await chatTools.GetToolsAsync(cancellationToken).ConfigureAwait(false);
        var functions = new Dictionary<string, AIFunction>(StringComparer.Ordinal);
        foreach (var function in tools.OfType<AIFunction>()) functions.TryAdd(function.Name, function);

        // 不按字符预算裁剪提示词：图片等媒体走 DataContent 多模态通道、不占字符预算，
        // 单条工具输出也已由各工具自己限长。此前的 TrimToBudget 是图片曾被误塞进正文
        // （而不是多模态内容）时的遗留兜底——那时 base64 进正文会把字符数直接撑爆。

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

                // 每完成一轮就产出一次累计用量：后续轮次可能被取消或抛错，届时已经花掉的 token 就再也报不出去了。
                // 命中率是 prompt 结构是否稳定的直接指标：缓存命中的部分按缓存价计费。
                Logger.Debug("LLM usage input={Input} output={Output} cached={Cached} hit={Hit:P0}",
                    inputTokens, outputTokens, cachedInputTokens,
                    inputTokens > 0 ? (double)cachedInputTokens / inputTokens : 0d);
                yield return new LlmUsageEvent(inputTokens, outputTokens, cachedInputTokens);
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
                    var label = ToolDisplay.Describe(functions.GetValueOrDefault(call.Name), call.Name, arguments);
                    var limitText = $"未执行：本次对话已达到最大工具调用轮次（{settings.MaxToolRounds} 轮）。";

                    yield return new LlmToolCallStartedEvent(call.CallId, call.Name, arguments, label);
                    messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, limitText)]));
                    yield return new LlmToolCallEvent(call.CallId, call.Name, arguments, limitText, label);
                }

                continue;
            }

            // 把模型的工具调用消息与本地执行结果接回历史，再请求下一轮
            messages.Add(assistantMessage);
            foreach (var call in calls)
            {
                var arguments = FormatArguments(call.Arguments);
                var label = ToolDisplay.Describe(functions.GetValueOrDefault(call.Name), call.Name, arguments);
                // 先通知界面「开始调用」，长耗时工具（MCP、命令执行）也能立刻看到反馈
                yield return new LlmToolCallStartedEvent(call.CallId, call.Name, arguments, label);

                object? result;
                using (ToolInvocationContext.Enter(call.CallId))
                {
                    result = await InvokeToolAsync(call, functions, cancellationToken).ConfigureAwait(false);
                }

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
                        yield return new LlmToolCallEvent(call.CallId, call.Name, arguments, summary, label);
                        continue;
                    }

                    // 纯文本内容集合：拼成字符串回传，避免被序列化成 JSON
                    if (text.Length > 0) result = text;
                }

                messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, result)]));
                yield return new LlmToolCallEvent(call.CallId, call.Name, arguments, FormatResult(result), label);
            }
        }
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
    /// 正文与思考统一去首尾空白，与落盘 / 重建请求历史用的文本一致，下一轮请求才能接上前缀缓存。
    /// </summary>
    private static ChatMessage BuildAssistantMessage(
        StringBuilder text,
        StringBuilder reasoning,
        List<FunctionCallContent> calls)
    {
        var thinking = reasoning.ToString().Trim();
        var body = text.ToString().Trim();
        var contents = new List<AIContent>(calls.Count + 2);
        if (thinking.Length > 0) contents.Add(new TextReasoningContent(thinking));
        // 有工具调用且没有正文时不再塞空 TextContent：落盘记录里也不会有独立正文行
        if (body.Length > 0 || calls.Count == 0) contents.Add(new TextContent(body));
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
        var directory = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "latest_request.json");
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
    /// 消息平台使用说明：只有当前接入了至少一个平台时才生成，否则返回空串（不注入系统提示词）。
    /// 平台可通过 <see cref="SoufflePet.Plugin.Sdk.IMessagePlatform.RolePrompt" /> 说明自身角色，
    /// 非空时汇总为「平台角色」一并注入；全部为空则不出现该段。
    /// </summary>
    private void BuildPlatformBlock(StringBuilder builder)
    {
        var entries = platforms.Platforms
            .Where(platform => !string.IsNullOrWhiteSpace(platform.Id))
            .GroupBy(platform => platform.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => (
                Id: group.Key,
                Role: group.Select(platform => platform.RolePrompt).FirstOrDefault(role => !string.IsNullOrWhiteSpace(role))))
            // 平台由插件注册，枚举顺序可能变化；固定成按 id 排序，保证这段系统提示词逐字节稳定
            .OrderBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (entries.Count == 0) return;

        builder.AppendLine();
        builder.AppendLine("## 消息平台接入");
        builder.AppendLine();
        builder.Append("已接入消息平台：").AppendLine(string.Join("、", entries.Select(entry => entry.Id)));

        var roles = entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Role)).ToList();
        if (roles.Count > 0)
        {
            builder.AppendLine("各平台角色（据此理解消息里的身份与语气）：");
            foreach (var entry in roles)
                builder.Append("- ").Append(entry.Id).Append('：').AppendLine(entry.Role!.Trim());
        }
    }

    /// <summary>
    /// 把工作目录下 AGENTS.md 的完整内容包成系统提示块追加到 builder；不缓存，每次对话重新读盘，
    /// 文件不存在、内容为空或读取失败时只输出工作区路径（失败只记日志，不影响本次对话）。
    /// </summary>
    private static void BuildWorkspaceBlock(StringBuilder builder)
    {
        // 按当前会话的逻辑工作目录读取，不依赖进程 cwd：进程 cwd 可能已被另一个会话占用
        var current = WorkspaceContext.Current;
        var path = Path.Combine(current, AgentsFileName);
        builder.AppendLine();
        builder.AppendLine("## 工作区");
        builder.AppendLine();
        builder.AppendLine($"当前工作区：'{current}'");
        try
        {
            if (!File.Exists(path)) return;

            var content = File.ReadAllText(path);
            if (content.Trim().Length != 0)
            {
                builder.AppendLine($"以下是工作区内 {AgentsFileName} 的内容，是本项目的约定与补充说明：");
                builder.AppendLine();
                builder.AppendLine(content.TrimEnd());
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "read {File} {Path} failed", AgentsFileName, path);
        }
    }

    /// <summary>截断过长的展示文本。</summary>
    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}