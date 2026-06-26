using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// In-memory admin broadcast messaging. Mirrors OpenList Go
/// <c>server/handles/message.go</c> SendAdminMessage / GetAdminMessage.
/// Admin posts a message; connected users poll <see cref="GetAsync"/> to receive it.
/// Not persisted — messages live only in memory for the broadcast window.
/// </summary>
public interface IBroadcastService
{
    /// <summary>Admin broadcasts a message. The body is a free-form { type, content } payload.</summary>
    Task<Resp> SendAsync(BroadcastMessage message, CancellationToken ct = default);
    /// <summary>Returns the latest broadcast message, or an empty 200 if none has been sent.</summary>
    Task<Resp<BroadcastMessage?>> GetAsync(CancellationToken ct = default);
}

/// <summary>
/// Broadcast payload. <c>Type</c> is a renderer hint consumed by the frontend
/// (e.g. "string", "image"); <c>Content</c> is the raw message body.
/// </summary>
public sealed class BroadcastMessage
{
    public string Type { get; set; } = "string";
    public object? Content { get; set; }
}
