// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SoufflePet.Plugin.Sdk;

namespace SoufflePet.Plugins.Loopback;

/// <summary>
/// 本地回环消息平台（仅用于本机调试）：
/// 在 127.0.0.1 上监听一个极简 HTTP 端口，外部脚本用
/// <c>GET /send?text=…</c> 就能把一条消息推给桌宠（等同于平台里被 @ 一次）；
/// 桌宠通过 <c>send_message</c> 发出的回复记录在 <c>GET /replies</c> 里，方便脚本断言。
/// 只监听回环地址，不对外暴露；没有平台角色说明（RolePrompt 为 null），不额外改动系统提示词。
/// </summary>
public sealed class LoopbackPlugin : PluginBase, IMessagePlatform
{
    private const int DefaultPort = 18765;
    private const int MaxConversations = 50;
    private const int MaxMessagesPerConversation = 200;
    private const int MaxReplies = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly Lock _gate = new();
    private readonly List<Conversation> _conversations = [];
    private readonly List<Reply> _replies = [];

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public string Id => "loopback";

    /// <summary>测试平台不注入角色说明，保持系统提示词干净（也便于对照缓存命中）。</summary>
    public string? RolePrompt => null;

    public event EventHandler<PlatformIncomingMessage>? OnMentioned;

    protected override Task OnInitializedAsync(CancellationToken cancellationToken)
    {
        var port = Context.Configuration?.TryGetProperty("port", out var value) == true
                   && value.TryGetInt32(out var configured)
            ? configured
            : DefaultPort;

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            _listener.Start();
        }
        catch (SocketException ex)
        {
            // 端口被占用只影响本插件，把错误写进日志后保持安静
            Context.Logger.LogError(ex, "loopback 无法监听 127.0.0.1:{Port}，插件不启动", port);
            _listener = null;
            _cts.Dispose();
            _cts = null;
            return Task.CompletedTask;
        }

        Context.Logger.LogInformation("loopback 消息平台已监听 http://127.0.0.1:{Port}/", port);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PlatformConversation>> GetConversationsAsync()
    {
        lock (_gate)
        {
            IReadOnlyList<PlatformConversation> result =
            [
                .. _conversations
                    .OrderByDescending(conversation => conversation.LastActive)
                    .Select(conversation => new PlatformConversation(conversation.Id, conversation.Id))
            ];
            return Task.FromResult(result);
        }
    }

    public Task<string> GetMessagesAsync(string conversationId, int limit = 20)
    {
        lock (_gate)
        {
            var conversation = FindConversation(conversationId);
            if (conversation is null) return Task.FromResult(string.Empty);

            var text = string.Join("\n", conversation.Messages
                .TakeLast(Math.Max(1, limit))
                .Select(message => $"[{message.Time:HH:mm:ss}] {message.Sender}：{message.Text}"));
            return Task.FromResult(text);
        }
    }

    public Task SendMessageAsync(string conversationId, string message)
    {
        lock (_gate)
        {
            _replies.Add(new Reply(DateTimeOffset.Now, conversationId, message));
            if (_replies.Count > MaxReplies) _replies.RemoveRange(0, _replies.Count - MaxReplies);
        }

        Context.Logger.LogInformation("loopback 桌宠回复 → {Conversation}：{Message}", conversationId, message);
        return Task.CompletedTask;
    }

    public Task SendAttachmentAsync(string conversationId, PlatformAttachment attachment)
        => SendMessageAsync(conversationId, $"[附件] {attachment.DisplayName ?? attachment.FilePath}");

    public override async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        _listener?.Stop();

        if (_acceptLoop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常停止
            }
        }

        _cts?.Dispose();
        _cts = null;
        _listener = null;
        _acceptLoop = null;
        Context.Logger.LogInformation("loopback 消息平台已停止");
    }

    /// <summary>接受连接：每个请求单独处理，出错只写日志，不影响监听循环。</summary>
    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Context.Logger.LogWarning(ex, "loopback 接受连接失败");
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                var requestLine = await ReadRequestHeadAsync(stream, cancellationToken).ConfigureAwait(false);
                var (path, query) = ParseRequestLine(requestLine);
                var body = HandleRequest(path, query);
                await WriteResponseAsync(stream, body, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Context.Logger.LogDebug(ex, "loopback 处理请求失败");
            }
        }
    }

    /// <summary>
    /// 读到空行为止（请求头读完）再返回请求行：只读一行就关闭会让未读的头部触发 TCP RST，
    /// 客户端（Invoke-WebRequest 等）会报「远程主机强迫关闭了一个现有的连接」。
    /// </summary>
    private static async Task<string> ReadRequestHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            length++;

            if (length >= 4
                && buffer[length - 1] == (byte)'\n'
                && buffer[length - 2] == (byte)'\r'
                && buffer[length - 3] == (byte)'\n'
                && buffer[length - 4] == (byte)'\r')
                break;

            if (length >= 2 && buffer[length - 1] == (byte)'\n' && buffer[length - 2] == (byte)'\n')
                break;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, length);
        return text.Split('\n', 2)[0].TrimEnd('\r');
    }

    private static (string Path, Dictionary<string, string> Query) ParseRequestLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var target = parts.Length >= 2 ? parts[1] : "/";
        var index = target.IndexOf('?');
        var path = index < 0 ? target : target[..index];
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (index >= 0)
        {
            foreach (var pair in target[(index + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=');
                var key = separator < 0 ? pair : pair[..separator];
                var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
                query[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace('+', ' '));
            }
        }

        return (path, query);
    }

    private string HandleRequest(string path, IReadOnlyDictionary<string, string> query) => path switch
    {
        "/health" => Json(new { ok = true, platform = Id }),
        "/send" => HandleSend(query),
        "/replies" => HandleReplies(query),
        "/messages" => HandleMessages(query),
        "/conversations" => HandleConversations(),
        _ => Json(new { ok = false, error = "unknown path" })
    };

    /// <summary><c>/send?text=…&amp;conversation=…&amp;sender=…</c>：把一条消息推给桌宠。</summary>
    private string HandleSend(IReadOnlyDictionary<string, string> query)
    {
        if (!query.TryGetValue("text", out var text) || string.IsNullOrWhiteSpace(text))
            return Json(new { ok = false, error = "text required" });

        var conversationId = query.TryGetValue("conversation", out var conversation) && conversation.Length > 0
            ? conversation
            : "test:probe";
        var sender = query.TryGetValue("sender", out var name) && name.Length > 0 ? name : "tester";

        // 正文以 [会话 id] 开头：系统提示词里约定模型据此知道往哪个会话回复
        var content = $"[{conversationId}] {text}";
        var messageId = Guid.NewGuid().ToString("N");
        RecordIncoming(conversationId, sender, content);

        Context.Logger.LogInformation("loopback 收到测试消息 conversation={Conversation} text={Text}", conversationId, text);
        OnMentioned?.Invoke(this, new PlatformIncomingMessage(Id, content, sender, messageId));
        return Json(new { ok = true, messageId });
    }

    /// <summary><c>/replies?since=N</c>：读取桌宠发回的回复（累计列表，since 为起始下标）。</summary>
    private string HandleReplies(IReadOnlyDictionary<string, string> query)
    {
        var since = query.TryGetValue("since", out var value) && int.TryParse(value, out var index)
            ? Math.Max(0, index)
            : 0;

        List<Reply> snapshot;
        lock (_gate) snapshot = [.. _replies.Skip(since)];

        return Json(new { ok = true, total = since + snapshot.Count, replies = snapshot });
    }

    /// <summary><c>/messages?conversation=…</c>：读取某个会话收到的消息。</summary>
    private string HandleMessages(IReadOnlyDictionary<string, string> query)
    {
        var conversationId = query.TryGetValue("conversation", out var value) && value.Length > 0
            ? value
            : "test:probe";

        lock (_gate)
        {
            var conversation = FindConversation(conversationId);
            var messages = conversation?.Messages.Select(message => new { message.Time, message.Sender, message.Text })
                ?? [];
            return Json(new { ok = true, conversation = conversationId, messages });
        }
    }

    private string HandleConversations()
    {
        lock (_gate)
        {
            var conversations = _conversations
                .OrderByDescending(conversation => conversation.LastActive)
                .Select(conversation => new { id = conversation.Id, messages = conversation.Messages.Count })
                .ToList();
            return Json(new { ok = true, conversations });
        }
    }

    private void RecordIncoming(string conversationId, string sender, string text)
    {
        lock (_gate)
        {
            var conversation = FindConversation(conversationId);
            if (conversation is null)
            {
                conversation = new Conversation(conversationId);
                _conversations.Add(conversation);
                if (_conversations.Count > MaxConversations)
                {
                    var stale = _conversations.OrderBy(item => item.LastActive).First();
                    _conversations.Remove(stale);
                }
            }

            conversation.LastActive = DateTimeOffset.Now;
            conversation.Messages.Add(new Incoming(DateTimeOffset.Now, sender, text));
            if (conversation.Messages.Count > MaxMessagesPerConversation)
                conversation.Messages.RemoveRange(0, conversation.Messages.Count - MaxMessagesPerConversation);
        }
    }

    private Conversation? FindConversation(string conversationId) =>
        _conversations.FirstOrDefault(conversation =>
            string.Equals(conversation.Id, conversationId, StringComparison.OrdinalIgnoreCase));

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static async Task WriteResponseAsync(NetworkStream stream, string body, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = "HTTP/1.1 200 OK\r\n"
                     + "Content-Type: application/json; charset=utf-8\r\n"
                     + $"Content-Length: {bytes.Length}\r\n"
                     + "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>一个测试会话的收发记录。</summary>
    private sealed class Conversation(string id)
    {
        public string Id { get; } = id;

        public List<Incoming> Messages { get; } = [];

        public DateTimeOffset LastActive { get; set; } = DateTimeOffset.Now;
    }

    /// <summary>一条收到的消息。</summary>
    private sealed record Incoming(DateTimeOffset Time, string Sender, string Text);

    /// <summary>桌宠通过 send_message 发出的一条回复。</summary>
    private sealed record Reply(DateTimeOffset Time, string Conversation, string Text);
}
