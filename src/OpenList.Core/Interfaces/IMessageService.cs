using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// 管理员消息系统：管理员与用户之间的持久化消息。
/// 与 OpenList Go 的 <c>server/handles/message.go</c> 对应。
/// </summary>
public interface IMessageService
{
    /// <summary>获取某用户的消息列表；可指定只取未读。</summary>
    Task<Resp<List<Message>>> GetAsync(long userId, bool? unreadOnly = null, CancellationToken ct = default);
    /// <summary>按消息 id 获取单条消息。</summary>
    Task<Resp<Message>> GetByIdAsync(long messageId, CancellationToken ct = default);
    /// <summary>向指定用户发送一条消息。</summary>
    Task<Resp> SendAsync(long userId, string title, string content, CancellationToken ct = default);
    /// <summary>将消息标记为已读。</summary>
    Task<Resp> MarkReadAsync(long messageId, CancellationToken ct = default);
    /// <summary>删除消息。</summary>
    Task<Resp> DeleteAsync(long messageId, CancellationToken ct = default);
}
