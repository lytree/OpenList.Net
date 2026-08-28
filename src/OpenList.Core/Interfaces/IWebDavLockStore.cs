namespace OpenList.Core.Interfaces;

/// <summary>
/// 内存版 WebDAV 锁存储。与 Go 的 <c>webdav.NewMemLS()</c> 行为对应：
/// 按路径维护带不透明 token 的独占锁，供 WebDAV 的 LOCK/UNLOCK 方法使用。
/// 采用单例生命周期——进程重启后锁会丢失（对单实例部署可接受，与 Go 一致）。
/// 多实例部署需要改用分布式存储。
/// </summary>
public interface IWebDavLockStore
{
    /// <summary>获取某路径的锁；未加锁或已过期时返回 null。</summary>
    Task<WebDavLock?> GetAsync(string path, CancellationToken ct = default);

    /// <summary>设置（替换）某路径的锁，会覆盖已有的锁。</summary>
    Task SetAsync(string path, WebDavLock @lock, CancellationToken ct = default);

    /// <summary>token 匹配时移除某路径的锁；未找到或 token 不匹配时返回 false。</summary>
    Task<bool> RemoveAsync(string path, string token, CancellationToken ct = default);
}

/// <summary>一条 WebDAV 锁记录。</summary>
public sealed record WebDavLock(
    string Token,       // 锁的 token
    string Scope,       // "exclusive"（唯一发放的作用域）
    string Depth,       // "0"、"1" 或 "infinity"
    int TimeoutSeconds, // 默认 3600
    DateTimeOffset CreatedAt); // 创建时间
