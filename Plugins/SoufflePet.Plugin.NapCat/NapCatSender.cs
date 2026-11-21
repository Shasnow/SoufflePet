// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SoufflePet.Plugin.Sdk;

namespace SoufflePet.Plugins.NapCat;

/// <summary>通过 OneBot action 向会话发送文本、图片与文件，并记录自己发出的消息 id。</summary>
internal sealed class NapCatSender
{
    /// <summary>图片作为消息段发送时的最大字节数（base64 编码会膨胀）。</summary>
    private const long MaxSendImageBytes = 5L * 1024 * 1024;

    /// <summary>文件走 base64 上传的上限；更大的文件传本地绝对路径，避免内存与 WebSocket 压力。</summary>
    private const long MaxBase64FileBytes = 10L * 1024 * 1024;

    /// <summary>配置正则的匹配超时：写出灾难性回溯时按「不分段 / 不过滤」降级，别把发送路径卡死。</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 回复开头的引用标记：[quote:被引用的原句]；兼容中文方括号与全角冒号。
    /// 原句允许出现成对的方括号（历史里的「[图片] 名称」「【转发】」这类），避免照抄附件行时标记失效。
    /// </summary>
    private static readonly Regex QuoteMarkerPattern = new(
        @"^\s*[\[【]\s*quote\s*[:：]\s*(?<quote>(?:[^\[\]【】]|\[[^\[\]【】]*\]|【[^\[\]【】]*】)+?)\s*[\]】]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    /// <summary>正文里的 @ 标记：[at:QQ号或昵称]；兼容中文方括号与全角冒号。</summary>
    private static readonly Regex AtMarkerPattern = new(
        @"[\[【]\s*at\s*[:：]\s*(?<target>[^\[\]【】]+?)\s*[\]】]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    /// <summary>@ 占位符的定界符：解析好的 at 目标先变成 \uE000QQ\uE001，跟着分段与拆分一起走。</summary>
    private const char AtOpen = '\uE000';

    private const char AtClose = '\uE001';

    private readonly ILogger _logger;
    private readonly NapCatConnection _connection;
    private readonly NapCatConversationStore _store;
    private readonly NapCatConfig _config;
    private readonly Func<long> _selfId;
    private readonly Regex? _segmentRegex;
    private readonly Regex? _filterRegex;

    /// <summary>自身昵称：首次发送时用 get_login_info 取一次，取不到就退回自己的 QQ 号。</summary>
    private string? _selfSender;

    /// <summary>昵称是否已经尝试解析过（失败也置位，避免每条消息都重试 get_login_info）。</summary>
    private bool _selfSenderResolved;

    public NapCatSender(
        ILogger logger,
        NapCatConnection connection,
        NapCatConversationStore store,
        NapCatConfig config,
        Func<long> selfId)
    {
        _logger = logger;
        _connection = connection;
        _store = store;
        _config = config;
        _selfId = selfId;
        _segmentRegex = CompileRegex(config.SegmentRegex, "分段正则");
        _filterRegex = CompileRegex(config.SegmentFilterRegex, "内容过滤正则");
    }

    /// <summary>
    /// 向指定会话发送文本：先解析开头的引用标记，再按分段正则切分并过滤内容，
    /// 每条之间按下一句的长度计算间隔（模拟打字速度），避免长回复瞬间刷屏。
    /// 带引用标记时，第一段会带上 reply 段引用对应消息。
    /// </summary>
    public async Task SendMessageAsync((string Kind, long Id) conversation, string message)
    {
        var socket = _connection.Socket;
        if (socket is not { State: WebSocketState.Open })
            throw new InvalidOperationException("NapCat WebSocket 未连接，消息没有发送。");

        var text = message.Trim();
        if (text.Length == 0) return;

        // 回复开头可以带 [quote:被引用的原句]，解析成真实引用；标记本身不发给对方
        var conversationId = ConversationId(conversation);
        var replyTo = ExtractQuoteMarker(ref text, conversationId);
        if (text.Length == 0) return;

        // [at:...] 先解析成占位符，避免分段时把标记切成两半
        text = PrepareAtMarkers(text, conversation, conversationId);

        // 超过长度上限时不分段（也不加分段延迟），只做内容过滤后整条发送
        var enableSegmentation = text.Length <= _config.SegmentMaxMessageChars;
        var chunks = BuildChunks(text, enableSegmentation);
        if (chunks.Count == 0) return;

        var isGroup = conversation.Kind == "group";
        var selfSender = await ResolveSelfSenderAsync(socket).ConfigureAwait(false);

        for (var index = 0; index < chunks.Count; index++)
        {
            // 间隔按「下一句」长度计算：分段模式下，发第 2 条起先等待与本条长度相关的时间
            if (index > 0 && enableSegmentation)
            {
                var delay = ComputeDelay(chunks[index]);
                if (delay > 0)
                    await Task.Delay(delay, _connection.ShutdownToken).ConfigureAwait(false);
            }

            var parameters = BuildTextParameters(
                conversation,
                chunks[index],
                index == 0 ? replyTo : null);

            try
            {
                var response = await _connection.SendActionAsync(
                        socket,
                        isGroup ? "send_group_msg" : "send_private_msg",
                        parameters)
                    .ConfigureAwait(false);

                // 自己说过的话也记进历史：模型读上下文、按内容引用自己的发言都靠它（需要 message_id）
                var sentId = GetSentMessageId(response);
                if (sentId is null) _logger.LogDebug("NapCat 发送响应缺少 message_id，本条自身消息无法被引用");
                _store.RecordSentMessage(
                    conversationId, sentId, DisplayText(chunks[index]), selfSender, _selfId());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 单段失败（含 at 段被平台拒绝）不中断后续分段，前面已发的内容照常保留
                _logger.LogWarning(ex, "发送第 {Index} 段消息失败", index + 1);
                continue;
            }
        }

        _logger.LogInformation(
            "已向{Kind} {Id} 发送 {Count} 段消息",
            isGroup ? "群" : "用户",
            conversation.Id,
            chunks.Count);
    }

    /// <summary>
    /// 解析回复开头的引用标记 [quote:被引用的原句]，返回对应消息 id，并把标记从文本里移除。
    /// 找不到匹配消息时只移除标记，按普通回复发送。
    /// </summary>
    private string? ExtractQuoteMarker(ref string text, string conversationId)
    {
        Match match;
        try
        {
            match = QuoteMarkerPattern.Match(text);
        }
        catch (RegexMatchTimeoutException ex)
        {
            _logger.LogWarning(ex, "解析引用标记超时，本次按普通回复发送");
            return null;
        }

        if (!match.Success) return null;

        var quote = match.Groups["quote"].Value.Trim();
        text = text[match.Length..].Trim();

        var messageId = _store.FindMessageIdByText(conversationId, quote);
        if (messageId is null)
            _logger.LogDebug("引用内容未匹配到最近的消息，按普通回复发送：{Quote}", quote);
        return messageId;
    }

    /// <summary>
    /// 把 @ 标记里的目标解析成 QQ 号：支持 all（@全体成员）、直接写 QQ 号，
    /// 以及按昵称在会话历史里反查；解析不出时返回 null。
    /// </summary>
    private string? ResolveAtTarget(string conversationId, string target)
    {
        var value = target.Trim();
        if (value.Length == 0) return null;

        if (value.Equals("all", StringComparison.OrdinalIgnoreCase)
            || value is "全体成员" or "所有人" or "全体")
            return "all";

        if (value.Length is >= 5 and <= 12
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var userId)
            && userId > 0)
            return userId.ToString(CultureInfo.InvariantCulture);

        // 排除桌宠自己：自消息现在也带自己的 QQ 号，否则 [at:自己的昵称] 会 @ 到自己
        var found = _store.FindUserIdBySender(conversationId, value, _selfId());
        return found > 0 ? found.ToString(CultureInfo.InvariantCulture) : null;
    }

    /// <summary>按分段正则切分，并移除过滤正则命中的内容。</summary>
    private List<string> BuildChunks(string text, bool enableSegmentation)
    {
        var segments = enableSegmentation && _segmentRegex is not null
            ? SplitAtMatches(text, _segmentRegex)
            : [text];

        var chunks = new List<string>();
        foreach (var raw in segments)
        {
            var segment = FilterText(raw).Trim();
            if (segment.Length > 0) chunks.Add(segment);
        }

        return chunks;
    }

    /// <summary>
    /// 按分段正则切分：只取匹配区间作为切分点，分隔符本身丢弃。
    /// 不用 <see cref="Regex.Split" />——它会把用户写的具名捕获组内容也塞进结果，标点会被当成一条消息发出去。
    /// 匹配超时（灾难性回溯）时退化为整条不切分。
    /// </summary>
    private string[] SplitAtMatches(string text, Regex regex)
    {
        var segments = new List<string>();
        var start = 0;
        try
        {
            foreach (Match match in regex.Matches(text))
            {
                if (match.Index > start) segments.Add(text[start..match.Index]);
                start = match.Index + match.Length;
            }
        }
        catch (RegexMatchTimeoutException ex)
        {
            _logger.LogWarning(ex, "分段正则匹配超时，本次不分段：{Pattern}", regex.ToString());
            return [text];
        }

        if (start < text.Length) segments.Add(text[start..]);
        return [.. segments];
    }

    /// <summary>应用内容过滤正则；匹配超时时保留原文，不影响发送。</summary>
    private string FilterText(string text)
    {
        if (_filterRegex is null) return text;

        try
        {
            return _filterRegex.Replace(text, string.Empty);
        }
        catch (RegexMatchTimeoutException ex)
        {
            _logger.LogWarning(ex, "内容过滤正则匹配超时，本次不过滤：{Pattern}", _filterRegex.ToString());
            return text;
        }
    }

    /// <summary>间隔 = 下一句字数 × 每字毫秒，受单次上限约束；配置为 0 时不等待。</summary>
    private int ComputeDelay(string nextChunk)
    {
        var perChar = _config.SegmentDelayMsPerChar;
        if (perChar <= 0) return 0;

        return Math.Min(nextChunk.Length * perChar, _config.SegmentMaxDelayMs);
    }

    /// <summary>编译配置里的正则；非法表达式记警告并忽略，避免影响发送。匹配超时按降级处理（见调用处）。</summary>
    private Regex? CompileRegex(string? pattern, string label)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;

        try
        {
            return new Regex(
                pattern,
                RegexOptions.Compiled | RegexOptions.Multiline,
                RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "{Label} 配置无效，已忽略：{Pattern}", label, pattern);
            return null;
        }
    }

    /// <summary>
    /// 发送附件：图片走消息段（base64），文件走 upload_group_file / upload_private_file；
    /// 小文件用 base64 避免路径权限差异，大文件保留绝对路径减少内存占用。
    /// </summary>
    public async Task SendAttachmentAsync(
        (string Kind, long Id) conversation,
        PlatformAttachment attachment)
    {
        var path = Path.GetFullPath(attachment.FilePath);
        if (!File.Exists(path))
            throw new InvalidOperationException($"文件不存在：{path}");

        var name = string.IsNullOrWhiteSpace(attachment.DisplayName)
            ? Path.GetFileName(path)
            : attachment.DisplayName.Trim();

        var socket = _connection.Socket;
        if (socket is not { State: WebSocketState.Open })
            throw new InvalidOperationException("NapCat WebSocket 未连接，附件没有发送。");

        var isGroup = conversation.Kind == "group";
        if (attachment.Kind == PlatformAttachmentKind.Image)
        {
            await SendImageAsync(socket, conversation, path, name).ConfigureAwait(false);
        }
        else
        {
            await SendFileAsync(socket, conversation, path, name).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "已向{Kind} {Id} 发送{Label}「{Name}」",
            isGroup ? "群" : "用户",
            conversation.Id,
            attachment.Kind == PlatformAttachmentKind.Image ? "图片" : "文件",
            name);
    }

    /// <summary>图片按 OneBot 消息段发送：数组格式避免 CQ 码转义，base64 不依赖 NapCat 的文件权限。</summary>
    private async Task SendImageAsync(
        ClientWebSocket socket,
        (string Kind, long Id) conversation,
        string path,
        string name)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxSendImageBytes)
            throw new InvalidOperationException(
                $"图片「{name}」大小为 {info.Length} 字节，超过 {MaxSendImageBytes} 的发送上限。");

        var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        var parameters = BuildMessageParameters(
            conversation,
            new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "image",
                    ["data"] = new Dictionary<string, object?>
                    {
                        ["file"] = "base64://" + Convert.ToBase64String(bytes),
                    },
                },
            });

        var response = await _connection.SendActionAsync(
                socket,
                conversation.Kind == "group" ? "send_group_msg" : "send_private_msg",
                parameters)
            .ConfigureAwait(false);

        _store.RecordSentMessage(
            ConversationId(conversation),
            GetSentMessageId(response),
            $"[图片] {name}",
            await ResolveSelfSenderAsync(socket).ConfigureAwait(false),
            _selfId());
    }

    /// <summary>文件走平台上传接口；小文件用 base64，大文件传绝对路径。</summary>
    private async Task SendFileAsync(
        ClientWebSocket socket,
        (string Kind, long Id) conversation,
        string path,
        string name)
    {
        var info = new FileInfo(path);
        var file = info.Length <= MaxBase64FileBytes
            ? "base64://" + Convert.ToBase64String(await File.ReadAllBytesAsync(path).ConfigureAwait(false))
            : path;

        var isGroup = conversation.Kind == "group";
        var parameters = new Dictionary<string, object?>
        {
            [isGroup ? "group_id" : "user_id"] = conversation.Id,
            ["file"] = file,
            ["name"] = name,
        };

        await _connection.SendActionAsync(
                socket,
                isGroup ? "upload_group_file" : "upload_private_file",
                parameters)
            .ConfigureAwait(false);

        _store.RecordSentMessage(
            ConversationId(conversation),
            null, // 上传接口不返回 message_id，文件消息无法被后续引用
            $"[文件] {name}",
            await ResolveSelfSenderAsync(socket).ConfigureAwait(false),
            _selfId());
    }

    /// <summary>把会话元组格式化成 group:群号 / private:QQ号 的会话 id。</summary>
    private static string ConversationId((string Kind, long Id) conversation)
        => $"{conversation.Kind}:{conversation.Id.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// 构造文本消息参数：正文里的 [at:...] 解析成 at 段，需要引用时再补 reply 段；
    /// 没有任何标记时沿用纯文本加 auto_escape，避免正文里的 CQ 码被平台当成消息段解析。
    /// </summary>
    private Dictionary<string, object?> BuildTextParameters(
        (string Kind, long Id) conversation,
        string text,
        string? replyToMessageId)
    {
        var parameters = new Dictionary<string, object?>
        {
            [conversation.Kind == "group" ? "group_id" : "user_id"] = conversation.Id,
        };

        var body = BuildMessageBody(text);
        if (string.IsNullOrWhiteSpace(replyToMessageId))
        {
            parameters["message"] = body;
            if (body is string) parameters["auto_escape"] = true;
            return parameters;
        }

        var segments = body as List<object> ?? [TextSegment((string)body)];
        segments.Insert(0, new Dictionary<string, object?>
        {
            ["type"] = "reply",
            ["data"] = new Dictionary<string, object?> { ["id"] = replyToMessageId },
        });
        parameters["message"] = segments;
        return parameters;
    }

    /// <summary>
    /// 把 [at:QQ号或昵称] 提前解析成占位符（\uE000QQ\uE001），
    /// 让标记跟着分段与按字数拆分一起走，避免被切点截断；解析不出目标时退回纯文本 @昵称。
    /// 私聊不支持 at，一律退回文本。
    /// </summary>
    private string PrepareAtMarkers(string text, (string Kind, long Id) conversation, string conversationId)
    {
        try
        {
            if (!AtMarkerPattern.IsMatch(text)) return text;

            return AtMarkerPattern.Replace(text, match =>
            {
                var target = match.Groups["target"].Value.Trim();
                var qq = conversation.Kind == "group" ? ResolveAtTarget(conversationId, target) : null;
                if (qq is null)
                {
                    _logger.LogDebug("未能解析 @ 目标，按纯文本发送：{Target}", target);
                    var next = match.Index + match.Length;
                    return next < text.Length && !char.IsWhiteSpace(text[next]) ? $"@{target} " : $"@{target}";
                }

                _logger.LogDebug("已解析 @ 目标：{Target} → {Qq}", target, qq);
                return $"{AtOpen}{qq}{AtClose}";
            });
        }
        catch (RegexMatchTimeoutException ex)
        {
            _logger.LogWarning(ex, "解析 @ 标记超时，本次按纯文本发送");
            return text;
        }
    }

    /// <summary>取自身昵称用作历史里的发送者：首次调用 get_login_info，失败时退回自己的 QQ 号（失败结果同样缓存，避免每次发送都重试）。</summary>
    private async Task<string?> ResolveSelfSenderAsync(ClientWebSocket socket)
    {
        if (_selfSenderResolved) return _selfSender;

        _selfSenderResolved = true;
        try
        {
            var response = await _connection.SendActionAsync(socket, "get_login_info", new Dictionary<string, object?>())
                .ConfigureAwait(false);
            if (response.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                var nickname = NapCatMessageParser.GetString(data, "nickname");
                if (string.IsNullOrWhiteSpace(nickname))
                {
                    var id = NapCatMessageParser.GetInt64(data, "user_id");
                    if (id > 0) nickname = id.ToString(CultureInfo.InvariantCulture);
                }

                if (!string.IsNullOrWhiteSpace(nickname)) _selfSender = nickname.Trim();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "获取自身昵称失败，历史里退回用 QQ 号记录");
        }

        if (_selfSender is { Length: > 0 }) return _selfSender;

        var selfId = _selfId();
        return selfId > 0 ? selfId.ToString(CultureInfo.InvariantCulture) : null;
    }

    /// <summary>把占位符还原成「@QQ号」，供会话历史记录使用。</summary>
    private static string DisplayText(string text)
    {
        if (text.IndexOf(AtOpen) < 0) return text;

        var builder = new StringBuilder(text.Length);
        var start = 0;
        while (start < text.Length)
        {
            var open = text.IndexOf(AtOpen, start);
            if (open < 0)
            {
                builder.Append(text, start, text.Length - start);
                break;
            }

            builder.Append(text, start, open - start);
            var close = text.IndexOf(AtClose, open + 1);
            if (close < 0)
            {
                builder.Append(text, open, text.Length - open);
                break;
            }

            builder.Append('@').Append(text, open + 1, close - open - 1);
            start = close + 1;
        }

        return builder.ToString();
    }

    /// <summary>把正文里的 at 占位符换成真实 at 段；没有占位符时返回原文本。</summary>
    private static object BuildMessageBody(string text)
    {
        if (text.IndexOf(AtOpen) < 0) return text;

        var segments = new List<object>();
        var start = 0;
        while (start < text.Length)
        {
            var open = text.IndexOf(AtOpen, start);
            if (open < 0)
            {
                segments.Add(TextSegment(text[start..]));
                break;
            }

            if (open > start) segments.Add(TextSegment(text[start..open]));

            var close = text.IndexOf(AtClose, open + 1);
            if (close < 0)
            {
                segments.Add(TextSegment(text[open..]));
                break;
            }

            segments.Add(new Dictionary<string, object?>
            {
                ["type"] = "at",
                ["data"] = new Dictionary<string, object?> { ["qq"] = text[(open + 1)..close] },
            });

            start = close + 1;
            // at 段后面紧跟正文时补一个空格，避免和昵称粘在一起
            if (start < text.Length && !char.IsWhiteSpace(text[start])) segments.Add(TextSegment(" "));
        }

        return segments;
    }

    /// <summary>构造一个纯文本消息段。</summary>
    private static Dictionary<string, object?> TextSegment(string text) => new()
    {
        ["type"] = "text",
        ["data"] = new Dictionary<string, object?> { ["text"] = text },
    };

    /// <summary>构造附件消息参数：附件用消息段数组，不设置 auto_escape。</summary>
    private static Dictionary<string, object?> BuildMessageParameters(
        (string Kind, long Id) conversation,
        object message) => new()
    {
        [conversation.Kind == "group" ? "group_id" : "user_id"] = conversation.Id,
        ["message"] = message,
    };

    /// <summary>从发送响应里取 message_id；没有时返回 null。</summary>
    private static string? GetSentMessageId(JsonElement response) =>
        response.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? NapCatMessageParser.GetString(data, "message_id")
            : null;
}
