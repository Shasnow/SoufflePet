using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SoufflePet.Plugin.Sdk;

namespace SoufflePet.Services;

/// <summary>一条需要桌宠处理的平台提醒；由插件推送后、交给对话层之前生成。</summary>
public sealed record PlatformNotification(string PlatformId, string Content, string? Sender = null);

/// <summary>
/// 消息平台服务：收集插件提供的消息平台，统一收发消息，
/// 并转发平台通过 <see cref="IMessagePlatform.OnMentioned" /> 主动上报的提醒。
/// </summary>
public class PlatformService : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<PlatformService>();

    /// <summary>用于推送去重的最近消息 id 数量上限。</summary>
    private const int RecentMessageLimit = 200;

    private readonly List<IMessagePlatform> _platforms = [];
    private readonly Dictionary<IMessagePlatform, EventHandler<PlatformIncomingMessage>> _subscriptions = [];
    private readonly Dictionary<string, DateTimeOffset> _recentMessages = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();
    private bool _disposed;

    /// <summary>插件推送了一条需要桌宠处理的消息；可能在后台线程触发。</summary>
    public event EventHandler<PlatformNotification>? Notified;

    /// <summary>已注册的消息平台快照（按注册顺序）。</summary>
    public IReadOnlyList<IMessagePlatform> Platforms
    {
        get { lock (_sync) return [.. _platforms]; }
    }

    /// <summary>注册插件实现的消息平台；重复注册同一实例时忽略，同时开始监听它的推送。</summary>
    public void Register(IMessagePlatform platform)
    {
        lock (_sync)
        {
            if (_disposed || _platforms.Any(p => ReferenceEquals(p, platform))) return;
            _platforms.Add(platform);
        }

        Attach(platform);
    }

    /// <summary>注销消息平台并停止监听推送（插件停用或应用退出时调用）。</summary>
    public void Unregister(IMessagePlatform platform)
    {
        Detach(platform);
        lock (_sync) _platforms.RemoveAll(p => ReferenceEquals(p, platform));
    }

    public bool HasPlatform(string platformId)
    {
        lock (_sync) return _platforms.Any(p => p.Id == platformId);
    }

    public async Task<Dictionary<string, string>> GetMessagesAsync()
    {
        var messages = new Dictionary<string, string>();
        foreach (var platform in Platforms)
        {
            messages[platform.Id] = await platform.ReceiveMessageAsync();
        }
        return messages;
    }

    public async Task<string> GetMessagesAsync(string platform)
    {
        var targetPlatform = Platforms.FirstOrDefault(p => p.Id == platform);
        if (targetPlatform != null)
        {
            return await targetPlatform.ReceiveMessageAsync();
        }
        return string.Empty;
    }

    public async Task SendMessageAsync(string platform, string message)
    {
        var targetPlatform = Platforms.FirstOrDefault(p => p.Id == platform);
        if (targetPlatform != null)
        {
            await targetPlatform.SendMessageAsync(message);
        }
    }

    /// <summary>读取指定平台某个会话的最近消息；返回可直接回传给模型的文本。</summary>
    public async Task<string> GetConversationMessagesAsync(string platform, string conversationId, int limit = 20)
    {
        var available = Platforms;
        if (available.Count == 0) return "当前没有可用的消息平台（没有插件提供消息平台）。";

        var id = platform.Trim();
        var target = available.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (target is null)
            return $"没有找到消息平台「{id}」，可用平台：{string.Join("、", available.Select(p => p.Id))}。";

        if (target is not IMessagePlatformConversation conversations)
            return $"平台「{target.Id}」不支持按会话读写消息，请改用 get_platform_messages / send_platform_message。";

        var conversation = conversationId.Trim();
        if (conversation.Length == 0) return "会话 id 不能为空。";

        var text = await conversations.GetConversationMessagesAsync(conversation, Math.Clamp(limit, 1, 50))
            .ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text)
            ? $"平台「{target.Id}」的会话「{conversation}」没有可读取的消息（会话 id 可能不正确，或还没有收到过该会话的消息）。"
            : $"平台「{target.Id}」会话「{conversation}」的最近消息：\n{text}";
    }

    /// <summary>向指定平台某个会话发送消息；返回可直接回传给模型的文本。</summary>
    public async Task<string> SendConversationMessageAsync(string platform, string conversationId, string message)
    {
        var available = Platforms;
        if (available.Count == 0) return "当前没有可用的消息平台（没有插件提供消息平台）。";

        var id = platform.Trim();
        var target = available.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (target is null)
            return $"没有找到消息平台「{id}」，可用平台：{string.Join("、", available.Select(p => p.Id))}。";

        if (target is not IMessagePlatformConversation conversations)
            return $"平台「{target.Id}」不支持按会话读写消息，请改用 get_platform_messages / send_platform_message。";

        var conversation = conversationId.Trim();
        if (conversation.Length == 0) return "会话 id 不能为空。";
        if (string.IsNullOrWhiteSpace(message)) return "消息内容不能为空。";

        await conversations.SendConversationMessageAsync(conversation, message).ConfigureAwait(false);
        return $"已向平台「{target.Id}」的会话「{conversation}」发送消息。";
    }

    /// <summary>订阅平台的提醒事件。</summary>
    private void Attach(IMessagePlatform platform)
    {
        EventHandler<PlatformIncomingMessage> handler = (_, message) => OnMessage(platform, message);
        lock (_sync)
        {
            if (_disposed || !_subscriptions.TryAdd(platform, handler)) return;
        }

        try
        {
            platform.OnMentioned += handler;
            Logger.Debug("platform push attached {Platform}", platform.Id);
        }
        catch (Exception ex)
        {
            lock (_sync) _subscriptions.Remove(platform);
            Logger.Warning(ex, "attach platform push {Platform} failed", platform.Id);
        }
    }

    /// <summary>退订提醒事件；插件停用前必须调用，避免事件持有已卸载程序集的引用。</summary>
    private void Detach(IMessagePlatform platform)
    {
        EventHandler<PlatformIncomingMessage>? handler;
        lock (_sync)
        {
            if (!_subscriptions.Remove(platform, out handler)) return;
        }

        try
        {
            platform.OnMentioned -= handler;
            Logger.Debug("platform push detached {Platform}", platform.Id);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "detach platform push {Platform} failed", platform.Id);
        }
    }

    private void OnMessage(IMessagePlatform platform, PlatformIncomingMessage message)
    {
        try
        {
            if (_disposed || string.IsNullOrWhiteSpace(message.Content)) return;

            var platformId = string.IsNullOrWhiteSpace(message.PlatformId) ? platform.Id : message.PlatformId.Trim();
            if (!Remember(platformId, message.MessageId)) return;

            var sender = string.IsNullOrWhiteSpace(message.Sender) ? null : message.Sender.Trim();
            var notification = new PlatformNotification(platformId, message.Content.Trim(), sender);

            Logger.Information("platform {Platform} pushed a message from {Sender}", platformId, sender ?? "未知");
            Notified?.Invoke(this, notification);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "handle platform push from {Platform} failed", platform.Id);
        }
    }

    /// <summary>记录消息 id；已见过时返回 false。没有 id 的消息平台无法去重，一律放行。</summary>
    private bool Remember(string platformId, string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return true;

        var key = $"{platformId}\0{messageId.Trim()}";
        lock (_sync)
        {
            if (_recentMessages.ContainsKey(key)) return false;

            _recentMessages[key] = DateTimeOffset.Now;
            if (_recentMessages.Count > RecentMessageLimit)
            {
                foreach (var stale in _recentMessages
                             .OrderBy(pair => pair.Value)
                             .Take(_recentMessages.Count - RecentMessageLimit)
                             .Select(pair => pair.Key)
                             .ToList())
                    _recentMessages.Remove(stale);
            }

            return true;
        }
    }

    public void Dispose()
    {
        List<KeyValuePair<IMessagePlatform, EventHandler<PlatformIncomingMessage>>> subscriptions;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            subscriptions = [.. _subscriptions];
            _subscriptions.Clear();
            _recentMessages.Clear();
            _platforms.Clear();
        }

        foreach (var (platform, handler) in subscriptions)
        {
            try
            {
                platform.OnMentioned -= handler;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "detach platform push on dispose failed");
            }
        }
    }
}
