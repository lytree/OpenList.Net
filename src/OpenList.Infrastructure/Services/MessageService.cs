using Microsoft.EntityFrameworkCore;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// Admin-to-user messaging. Mirrors OpenList Go <c>internal/op/message.go</c>.
/// </summary>
public sealed class MessageService : IMessageService
{
    private readonly OpenListDbContext _db;
    private readonly TimeProvider _time;

    public MessageService(OpenListDbContext db, TimeProvider? time = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
    }

    public async Task<Resp<List<Message>>> GetAsync(long userId, bool? unreadOnly = null, CancellationToken ct = default)
    {
        var q = _db.Messages.AsNoTracking().Where(m => m.UserId == userId);
        if (unreadOnly == true) q = q.Where(m => !m.Read);
        // Order by Id desc (auto-increment preserves chronological order; SQLite
        // does not support DateTimeOffset in ORDER BY clauses).
        var list = await q.OrderByDescending(m => m.Id).ToListAsync(ct);
        return Resp<List<Message>>.Ok(list);
    }

    public async Task<Resp<Message>> GetByIdAsync(long messageId, CancellationToken ct = default)
    {
        var msg = await _db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (msg is null) return Resp<Message>.Fail("message not found", RespCodes.NotFound);
        return Resp<Message>.Ok(msg);
    }

    public async Task<Resp> SendAsync(long userId, string title, string content, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content))
            return Resp.Fail("title and content required", RespCodes.BadRequest);

        _db.Messages.Add(new Message
        {
            UserId = userId,
            Title = title,
            Content = content,
            Read = false,
            CreatedAt = _time.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    public async Task<Resp> MarkReadAsync(long messageId, CancellationToken ct = default)
    {
        var msg = await _db.Messages.FindAsync([messageId], ct);
        if (msg is null) return Resp.Fail("message not found", RespCodes.NotFound);
        msg.Read = true;
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    public async Task<Resp> DeleteAsync(long messageId, CancellationToken ct = default)
    {
        var msg = await _db.Messages.FindAsync([messageId], ct);
        if (msg is null) return Resp.Fail("message not found", RespCodes.NotFound);
        _db.Messages.Remove(msg);
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }
}
