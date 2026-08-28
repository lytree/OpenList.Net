using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 内存版管理端广播实现：仅保存最近一条广播，使轮询客户端都能看到同一条消息。
/// 对应 Go 参考实现中“管理端消息为单一共享槽位而非队列”的行为。
/// </summary>
public sealed class BroadcastService : IBroadcastService
{
    private readonly object _lock = new();
    private BroadcastMessage? _current;
    private DateTimeOffset _sentAt;

    /// <summary>
    /// 发送（覆盖）当前广播消息。
    /// </summary>
    /// <param name="message">广播消息；为 null 时返回 400。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回 Resp.Ok()。</returns>
    public Task<Resp> SendAsync(BroadcastMessage message, CancellationToken ct = default)
    {
        if (message is null) return Task.FromResult(Resp.Fail("message is required", RespCodes.BadRequest));
        lock (_lock)
        {
            _current = message;
            _sentAt = DateTimeOffset.UtcNow;
        }
        return Task.FromResult(Resp.Ok());
    }

    /// <summary>
    /// 获取最近一次的广播消息。
    /// </summary>
    public Task<Resp<BroadcastMessage?>> GetAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(Resp<BroadcastMessage?>.Ok(_current));
        }
    }
}
