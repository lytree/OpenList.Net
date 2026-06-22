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

public sealed class Message
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool Read { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
