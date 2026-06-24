namespace OpenList.Core.Models;

/// <summary>
/// Admin-to-user message. Mirrors OpenList Go <c>server/model/message.go</c>.
/// </summary>
public sealed class Message
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool Read { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
