using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.Services;

public class MessageService : IMessageService
{
    private readonly List<Message> _messages = [];

    public Task SendMessageAsync(uint userId, string title, string content, CancellationToken ct = default)
    {
        _messages.Add(new Message
        {
            Id = (uint)(_messages.Count + 1),
            UserId = userId,
            Title = title,
            Content = content,
            CreatedAt = DateTime.UtcNow
        });
        return Task.CompletedTask;
    }

    public Task<List<Message>> GetMessagesAsync(uint userId, CancellationToken ct = default)
    {
        return Task.FromResult(_messages.Where(m => m.UserId == userId).ToList());
    }
}
