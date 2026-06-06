using Microsoft.EntityFrameworkCore;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

public class MessageService : IMessageService
{
    private readonly OpenListDbContext _db;

    public MessageService(OpenListDbContext db) => _db = db;

    public async Task SendMessageAsync(uint userId, string title, string content, CancellationToken ct = default)
    {
        var record = new MessageRecord
        {
            UserId = userId,
            Type = "notification",
            Title = title,
            Content = content,
            CreatedAt = DateTime.UtcNow
        };
        _db.Messages.Add(record);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<List<Message>> GetMessagesAsync(uint userId, CancellationToken ct = default)
    {
        return await _db.Messages
            .Where(m => m.UserId == userId)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new Message
            {
                Id = (uint)m.Id,
                UserId = m.UserId,
                Title = m.Title,
                Content = m.Content,
                Read = m.Read,
                CreatedAt = m.CreatedAt
            })
            .ToListAsync(ct);
    }
}
