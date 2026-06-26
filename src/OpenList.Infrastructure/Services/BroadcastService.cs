using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// In-memory admin broadcast implementation. Holds only the most recent broadcast
/// so polling clients all see the same message. Mirrors OpenList Go's behavior
/// where the admin message is a single shared slot rather than a queue.
/// </summary>
public sealed class BroadcastService : IBroadcastService
{
    private readonly object _lock = new();
    private BroadcastMessage? _current;
    private DateTimeOffset _sentAt;

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

    public Task<Resp<BroadcastMessage?>> GetAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(Resp<BroadcastMessage?>.Ok(_current));
        }
    }
}
