using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SoufflePet.Plugin.Sdk;

/// <summary>
/// 插件主动上报的一条平台消息。是否值得提醒桌宠（例如消息里 @ 了桌宠）由插件自行判断：
/// 只有插件认为需要唤醒宿主时，才通过 <see cref="IMessagePlatform.OnMentioned" /> 抛出。
/// </summary>
/// <param name="PlatformId">消息平台 id，通常与 <see cref="IMessagePlatform.Id" /> 一致。</param>
/// <param name="Content">
/// 消息正文，**不要**再拼「[会话 id] 发送者：」这类前缀，也**不要**拼「@你」「引用消息」这类装饰：
/// 会话 id 与发送者分别由 <paramref name="ConversationId" /> / <paramref name="Sender" /> 表达，
/// 「@ 了桌宠」与「引用」由 <paramref name="MentionsSelf" /> / <paramref name="Quote" /> 表达，
/// 宿主自己组装展示行与系统提示词。正文保持干净还有一个硬性原因：宿主据此判断消息是否是一条
/// 命令（正文以 <c>/</c> 开头），前缀会把 <c>/model</c> 顶到第二位，命令分支就永远不命中。
/// </param>
/// <param name="Sender">发送者昵称（给人看的显示名），未知时为 null。</param>
/// <param name="MessageId">平台侧的消息 id，用于宿主去重；平台没有 id 时可为 null。</param>
/// <param name="Context">触发消息前的最近对话，供模型理解上下文；没有上下文时为 null。</param>
/// <param name="SenderId">
/// 发送者在平台内的稳定 id（如 QQ 号、openid），用于命令的管理员判定；
/// 平台拿不到稳定 id 时为 null，此时该发送者一律不被视为管理员。
/// </param>
/// <param name="ConversationId">消息所属会话 id，与 <see cref="PlatformConversation.ConversationId" /> 同一套写法；未知时为 null。</param>
/// <param name="MentionsSelf">
/// 这条消息是否直接指向桌宠：@ 了桌宠，或引用了桌宠自己说过的话。只放标记，不要往正文里拼「@你」。
/// </param>
/// <param name="Quote">
/// 被引用消息的摘要，形如「发送者：正文摘要」；没有引用或反查失败时为 null。同样不要拼进正文。
/// </param>
public sealed record PlatformIncomingMessage(
    string PlatformId,
    string Content,
    string? Sender = null,
    string? MessageId = null,
    string? Context = null,
    string? SenderId = null,
    string? ConversationId = null,
    bool MentionsSelf = false,
    string? Quote = null);

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
    /// 平台提示词：告诉模型这个平台在它眼里的身份与额外约定，
    /// 例如「这是你自己的 QQ」「你正在托管主人的 QQ」，或平台特有的消息写法（引用、@ 等）。
    /// 宿主会把它注入系统提示词；返回 null 或空白时，该平台不注入提示词。
    /// </summary>
    string? PlatformPrompt { get; }

    /// <summary>
    /// 列出当前已知的会话（按最近活跃排序）。插件可以只返回本次运行期间收到过消息的会话。
    /// </summary>
    Task<IReadOnlyList<PlatformConversation>> GetConversationsAsync();

    /// <summary>
    /// 读取指定会话最近的消息。返回的文本由插件决定格式，但应带上发送者（形如「发送者：正文」）；
    /// 会话 id 由调用方给出，**不要**在每一行里重复——读历史 / 唤醒上下文本来就在该会话的上下文里。
    /// </summary>
    /// <param name="conversationId">会话 id，取自 <see cref="GetConversationsAsync" /> 或 <see cref="PlatformIncomingMessage.ConversationId" />。</param>
    /// <param name="limit">最多返回的消息条数。</param>
    Task<string> GetMessagesAsync(string conversationId, int limit = 20);

    /// <summary>向指定会话发送消息。</summary>
    /// <param name="conversationId">会话 id，取自 <see cref="GetConversationsAsync" /> 或 <see cref="PlatformIncomingMessage.ConversationId" />。</param>
    /// <param name="message">要发送的消息内容。</param>
    Task SendMessageAsync(string conversationId, string message);

    /// <summary>向指定会话发送图片或文件。</summary>
    /// <param name="conversationId">会话 id，取自 <see cref="GetConversationsAsync" /> 或 <see cref="PlatformIncomingMessage.ConversationId" />。</param>
    /// <param name="attachment">要发送的附件。</param>
    Task SendAttachmentAsync(string conversationId, PlatformAttachment attachment);

    /// <summary>
    /// 某个发送者能否在平台消息里执行命令（<c>/model</c>、<c>/stop</c>、<c>/help</c>）。
    /// 宿主只在返回 true 时执行，并把命令结果回执到原会话；<paramref name="senderId" /> 即
    /// <see cref="PlatformIncomingMessage.SenderId" />，由插件拿自己的配置（例如管理员 QQ 号列表）比对。
    /// 默认实现一律拒绝：没有实现该方法的平台不能用命令触发任何操作。
    /// </summary>
    bool AllowsPlatformCommands(string? senderId) => false;

    /// <summary>
    /// 插件判定需要唤醒宿主时触发（如消息 @ 了桌宠）；可能在任意后台线程触发。
    /// 插件负责判定「新消息」与「是否需要提醒」，并自行忽略自己发出的消息。
    /// </summary>
    event EventHandler<PlatformIncomingMessage>? OnMentioned;
}
