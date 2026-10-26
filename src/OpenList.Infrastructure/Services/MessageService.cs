using Microsoft.EntityFrameworkCore;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 管理端向用户发送消息的服务。对应 Go 参考实现 <c>internal/op/message.go</c>。
/// </summary>
public sealed class MessageService : IMessageService
{
    private readonly OpenListDbContext _db;
    private readonly TimeProvider _time;

    /// <summary>
    /// 创建消息服务实例。
    /// </summary>
    /// <param name="db">EF Core 数据库上下文。</param>
    /// <param name="time">时间源（可选，默认系统时区）。</param>
    public MessageService(OpenListDbContext db, TimeProvider? time = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// 获取指定用户的消息列表（可按未读过滤），按 Id 倒序。
    /// </summary>
    public async Task<Resp<List<Message>>> GetAsync(long userId, bool? unreadOnly = null, CancellationToken ct = default)
    {
        var q = _db.Messages.AsNoTracking().Where(m => m.UserId == userId);
        if (unreadOnly == true) q = q.Where(m => !m.Read);
        // 按 Id 倒序（自增 Id 保留时间顺序；SQLite 的 ORDER BY 不支持 DateTimeOffset）。
        var list = await q.OrderByDescending(m => m.Id).ToListAsync(ct);
        return Resp<List<Message>>.Ok(list);
    }

    /// <summary>
    /// 按 Id 获取单条消息。
    /// </summary>
    public async Task<Resp<Message>> GetByIdAsync(long messageId, CancellationToken ct = default)
    {
        var msg = await _db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (msg is null) return Resp<Message>.Fail("message not found", RespCodes.NotFound);
        return Resp<Message>.Ok(msg);
    }

    /// <summary>
    /// 向用户发送一条消息（标题与正文必填）。
    /// </summary>
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

    /// <summary>
    /// 将指定消息标记为已读。
    /// </summary>
    public async Task<Resp> MarkReadAsync(long messageId, long? ownerUserId = null, CancellationToken ct = default)
    {
        var msg = await _db.Messages.FindAsync([messageId], ct);
        if (msg is null) return Resp.Fail("message not found", RespCodes.NotFound);

        // 归属校验：/me/message/read 走的就是这条路。原先没有这道检查，任何登录用户
        // 猜到（自增）id 就能把别人的消息标成已读 —— 不泄露正文，但能静默清掉对方的
        // 未读徽标，「未读」这个信号就不可信了。
        // 越权返回 NotFound 而不是 Forbidden：Forbidden 等于确认「这个 id 存在」。
        if (ownerUserId is { } owner && msg.UserId != owner)
            return Resp.Fail("message not found", RespCodes.NotFound);

        msg.Read = true;
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    /// <summary>
    /// 删除指定消息。
    /// </summary>
    public async Task<Resp> DeleteAsync(long messageId, CancellationToken ct = default)
    {
        var msg = await _db.Messages.FindAsync([messageId], ct);
        if (msg is null) return Resp.Fail("message not found", RespCodes.NotFound);
        _db.Messages.Remove(msg);
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }
}
