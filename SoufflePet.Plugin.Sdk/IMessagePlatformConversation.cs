using System.Collections.Generic;
using System.Threading.Tasks;

namespace SoufflePet.Plugin.Sdk;

/// <summary>
/// 消息平台里的一个会话（私聊或群聊）。
/// </summary>
/// <param name="ConversationId">
/// 平台内唯一的会话 id，读写消息时原样回传。建议使用 kind:id 形式，例如 NapCat 的 group:12345、private:777。
/// </param>
/// <param name="DisplayName">会话展示名，例如群名或对方昵称；未知时为 null。</param>
public sealed record PlatformConversation(string ConversationId, string? DisplayName = null);

/// <summary>
/// 按会话读写消息的能力：平台可以实现该接口，让宿主按会话 id 精确读取或发送消息。
/// 不实现时，宿主只能使用 <see cref="IMessagePlatform" /> 的最新消息收发。
/// </summary>
public interface IMessagePlatformConversation
{
    /// <summary>
    /// 列出当前已知的会话（按最近活跃排序）。插件可以只返回本次运行期间收到过消息的会话。
    /// </summary>
    Task<IReadOnlyList<PlatformConversation>> GetConversationsAsync();

    /// <summary>
    /// 读取指定会话最近的消息。返回的文本由插件决定格式，但应带上会话 id 与发送者，便于模型理解上下文。
    /// </summary>
    /// <param name="conversationId">会话 id，取自 <see cref="GetConversationsAsync" /> 或消息前缀。</param>
    /// <param name="limit">最多返回的消息条数。</param>
    Task<string> GetConversationMessagesAsync(string conversationId, int limit = 20);

    /// <summary>向指定会话发送消息。</summary>
    /// <param name="conversationId">会话 id，取自 <see cref="GetConversationsAsync" /> 或消息前缀。</param>
    /// <param name="message">要发送的消息内容。</param>
    Task SendConversationMessageAsync(string conversationId, string message);
}
