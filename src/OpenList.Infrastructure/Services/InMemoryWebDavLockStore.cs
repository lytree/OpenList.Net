using System.Collections.Concurrent;
using OpenList.Core.Interfaces;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 基于 <see cref="ConcurrentDictionary{TKey, TValue}"/> 的单例内存版
/// <see cref="IWebDavLockStore"/>。过期的锁在访问时惰性清理（无后台清理线程）。
/// 对应 Go 的 <c>webdav.NewMemLS()</c>，适用于单实例部署。
/// </summary>
public sealed class InMemoryWebDavLockStore : IWebDavLockStore
{
    private readonly ConcurrentDictionary<string, WebDavLock> _locks = new();

    /// <summary>
    /// 获取指定路径的锁；若锁已过期则删除并返回 null。
    /// </summary>
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

    /// <summary>
    /// 为指定路径写入/覆盖一个锁。
    /// </summary>
    public Task SetAsync(string path, WebDavLock @lock, CancellationToken ct = default)
    {
        _locks[path] = @lock;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 移除指定路径的锁：仅当锁存在、未过期且 Token 匹配时删除成功。
    /// </summary>
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
