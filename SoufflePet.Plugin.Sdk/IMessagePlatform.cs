using System;
using System.Collections.Generic;
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
/// 消息平台里的一个会话（私聊或群聊）。
/// </summary>
/// <param name="ConversationId">
/// 平台内唯一的会话 id，读写消息时原样回传。建议使用 kind:id 形式，例如 NapCat 的 group:12345、private:777。
/// </param>
/// <param name="DisplayName">会话展示名，例如群名或对方昵称；未知时为 null。</param>
public sealed record PlatformConversation(string ConversationId, string? DisplayName = null);

/// <summary>要发送到消息平台的附件类型。</summary>
public enum PlatformAttachmentKind
{
    /// <summary>图片：平台按图片消息发送，可直接预览。</summary>
    Image,

    /// <summary>普通文件：平台按文件消息发送。</summary>
    File,
}

/// <summary>
/// 要发送的附件：只传本地绝对路径，由插件自行读取并上传，
/// 避免二进制内容经过模型上下文或宿主再序列化一次。
/// </summary>
/// <param name="FilePath">本地文件的绝对路径。</param>
/// <param name="Kind">附件类型。</param>
/// <param name="DisplayName">展示文件名；为空时由插件按路径推断。</param>
public sealed record PlatformAttachment(
    string FilePath,
    PlatformAttachmentKind Kind,
    string? DisplayName = null);

/// <summary>
/// 消息平台接口：插件通过实现该接口接入不同的消息平台（例如 Discord、Slack），
/// 消息收发统一以「平台 + 会话」为入口，不使用隐式的「最近会话」。
/// </summary>
public interface IMessagePlatform
{
    /// <summary>消息平台的唯一标识符，例如 napcat。</summary>
    string Id { get; }

    /// <summary>
    /// 平台角色说明：告诉模型这个平台在它眼里的身份，例如「这是你自己的 QQ」或「你正在托管主人的 QQ」。
    /// 宿主会把它注入系统提示词，模型据此理解平台消息里的身份与语气；
    /// 返回 null 或空白时，该平台不注入角色说明。
    /// </summary>
    string? RolePrompt { get; }

    /// <summary>
    /// 列出当前已知的会话（按最近活跃排序）。插件可以只返回本次运行期间收到过消息的会话。
    /// </summary>
    Task<IReadOnlyList<PlatformConversation>> GetConversationsAsync();

    /// <summary>
    /// 读取指定会话最近的消息。返回的文本由插件决定格式，但应带上会话 id 与发送者，便于模型理解上下文。
    /// </summary>
    /// <param name="conversationId">会话 id，取自 <see cref="GetConversationsAsync" /> 或消息前缀。</param>
    /// <param name="limit">最多返回的消息条数。</param>
    Task<string> GetMessagesAsync(string conversationId, int limit = 20);

    /// <summary>向指定会话发送消息。</summary>
    /// <param name="conversationId">会话 id，取自 <see cref="GetConversationsAsync" /> 或消息前缀。</param>
    /// <param name="message">要发送的消息内容。</param>
    Task SendMessageAsync(string conversationId, string message);

    /// <summary>向指定会话发送图片或文件。</summary>
    /// <param name="conversationId">会话 id，取自 <see cref="GetConversationsAsync" /> 或消息前缀。</param>
    /// <param name="attachment">要发送的附件。</param>
    Task SendAttachmentAsync(string conversationId, PlatformAttachment attachment);

    /// <summary>
    /// 插件判定需要唤醒宿主时触发（如消息 @ 了桌宠）；可能在任意后台线程触发。
    /// 插件负责判定「新消息」与「是否需要提醒」，并自行忽略自己发出的消息。
    /// </summary>
    event EventHandler<PlatformIncomingMessage>? OnMentioned;
}
