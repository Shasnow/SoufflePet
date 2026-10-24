using System;
using System.Threading.Tasks;

namespace SoufflePet.Plugin.Sdk;

/// <summary>
/// 插件主动上报的一条平台消息。是否值得提醒桌宠（例如消息里 @ 了桌宠）由插件自行判断：
/// 只有插件认为需要唤醒宿主时，才通过 <see cref="IMessagePlatform.OnMentioned" /> 抛出。
/// </summary>
/// <param name="PlatformId">消息平台 id，通常与 <see cref="IMessagePlatform.Id" /> 一致。</param>
/// <param name="Content">消息正文。</param>
/// <param name="Sender">发送者昵称，未知时为 null。</param>
/// <param name="MessageId">平台侧的消息 id，用于宿主去重；平台没有 id 时可为 null。</param>
public sealed record PlatformIncomingMessage(
    string PlatformId,
    string Content,
    string? Sender = null,
    string? MessageId = null);

/// <summary>
/// 消息平台接口：插件可以通过实现该接口来支持不同的消息平台，例如 Discord、Slack 等。
/// </summary>
public interface IMessagePlatform
{
    /// <summary>
    /// 发送消息的异步方法。插件可以实现该方法来向消息平台发送消息。
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    Task SendMessageAsync(string message);
    
    /// <summary>
    /// 接收消息的异步方法。插件可以实现该方法来从消息平台接收消息。
    /// 如果有多条消息，插件可以选择返回最新的一条消息，或者将多条消息合并成一条返回。
    /// </summary>
    /// <returns></returns>
    Task<string> ReceiveMessageAsync();
    
    /// <summary>
    /// 获取消息平台的唯一标识符。插件可以实现该属性来标识自己支持的消息平台。
    /// </summary>
    string Id { get; }

    /// <summary>
    /// 插件判定需要唤醒宿主时触发（如消息 @ 了桌宠）；可能在任意后台线程触发。
    /// 插件负责判定「新消息」与「是否需要提醒」，并自行忽略自己发出的消息。
    /// </summary>
    event EventHandler<PlatformIncomingMessage>? OnMentioned;
}