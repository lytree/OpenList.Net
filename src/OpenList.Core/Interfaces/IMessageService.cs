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
    /// <summary>
    /// 将消息标记为已读。
    /// </summary>
    /// <param name="messageId">消息 id。</param>
    /// <param name="ownerUserId">
    /// 非空时要求消息属于该用户，否则返回 404（用户侧调用必须传，见 /me/message/read）；
    /// 空表示不校验归属（管理端调用）。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    Task<Resp> MarkReadAsync(long messageId, long? ownerUserId = null, CancellationToken ct = default);
    /// <summary>删除消息。</summary>
    Task<Resp> DeleteAsync(long messageId, CancellationToken ct = default);
}
