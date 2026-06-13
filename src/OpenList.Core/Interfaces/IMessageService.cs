namespace OpenList.Core.Interfaces;

public interface IMessageService
{
    Task SendMessageAsync(uint userId, string title, string content, CancellationToken ct = default);
    Task<List<Message>> GetMessagesAsync(uint userId, CancellationToken ct = default);
}

public class Message
{
    public uint Id { get; set; }
    public uint UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool Read { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
