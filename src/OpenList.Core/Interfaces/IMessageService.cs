using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// Admin message system: persistent messages between admin and users.
/// Mirrors OpenList Go <c>server/handles/message.go</c>.
/// </summary>
public interface IMessageService
{
    Task<Resp<List<Message>>> GetAsync(long userId, bool? unreadOnly = null, CancellationToken ct = default);
    Task<Resp> SendAsync(long userId, string title, string content, CancellationToken ct = default);
    Task<Resp> MarkReadAsync(long messageId, CancellationToken ct = default);
}
