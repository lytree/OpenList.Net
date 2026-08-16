namespace OpenList.Core.Interfaces;

/// <summary>
/// In-memory WebDAV lock store. Mirrors Go's <c>webdav.NewMemLS()</c> behaviour:
/// per-path exclusive locks with opaque tokens, used by WebDAV LOCK/UNLOCK methods.
/// Singleton lifetime — locks are lost on process restart (acceptable for single-instance
/// deployments, same as Go). Multi-instance deployments would need a distributed store.
/// </summary>
public interface IWebDavLockStore
{
    /// <summary>Get the lock for a path, or null if unlocked (or expired).</summary>
    Task<WebDavLock?> GetAsync(string path, CancellationToken ct = default);

    /// <summary>Set (replace) the lock for a path. Overwrites any existing lock.</summary>
    Task SetAsync(string path, WebDavLock @lock, CancellationToken ct = default);

    /// <summary>Remove the lock for a path if the token matches. Returns false if not found or token mismatch.</summary>
    Task<bool> RemoveAsync(string path, string token, CancellationToken ct = default);
}

/// <summary>A WebDAV lock entry.</summary>
public sealed record WebDavLock(
    string Token,
    string Scope,          // "exclusive" (the only scope we issue)
    string Depth,          // "0", "1", or "infinity"
    int TimeoutSeconds,     // 3600 by default
    DateTimeOffset CreatedAt);
