using System.Collections.Concurrent;
using OpenList.Core.Interfaces;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// Singleton in-memory <see cref="IWebDavLockStore"/> backed by a <see cref="ConcurrentDictionary{TKey, TValue}"/>.
/// Expired locks are cleaned up lazily on access (no background sweep). Mirrors Go's
/// <c>webdav.NewMemLS()</c> for single-instance deployments.
/// </summary>
public sealed class InMemoryWebDavLockStore : IWebDavLockStore
{
    private readonly ConcurrentDictionary<string, WebDavLock> _locks = new();

    public Task<WebDavLock?> GetAsync(string path, CancellationToken ct = default)
    {
        if (_locks.TryGetValue(path, out var @lock))
        {
            if (IsExpired(@lock))
            {
                _locks.TryRemove(path, out _);
                return Task.FromResult<WebDavLock?>(null);
            }
            return Task.FromResult<WebDavLock?>(@lock);
        }
        return Task.FromResult<WebDavLock?>(null);
    }

    public Task SetAsync(string path, WebDavLock @lock, CancellationToken ct = default)
    {
        _locks[path] = @lock;
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string path, string token, CancellationToken ct = default)
    {
        if (_locks.TryGetValue(path, out var existing))
        {
            if (IsExpired(existing))
            {
                _locks.TryRemove(path, out _);
                return Task.FromResult(false);
            }
            if (existing.Token == token)
            {
                _locks.TryRemove(path, out _);
                return Task.FromResult(true);
            }
        }
        return Task.FromResult(false);
    }

    private static bool IsExpired(WebDavLock @lock) =>
        @lock.TimeoutSeconds > 0 &&
        DateTimeOffset.UtcNow - @lock.CreatedAt > TimeSpan.FromSeconds(@lock.TimeoutSeconds);
}
