using System.Collections.Concurrent;

namespace OpenList.Drivers.Cloud;

/// <summary>
/// 阿里云盘请求配额的三档分类，对齐 Go <c>drivers/aliyundrive_open/limiter.go</c> 的
/// <c>limiterType</c>。三类请求在阿里云侧是**独立配额**，因此分别限流。
/// </summary>
public enum AliyunLimiterKind
{
    /// <summary>列目录（<c>openFile/list</c>）：文档 4 req/s。</summary>
    List,

    /// <summary>取下载链接（<c>openFile/getDownloadUrl</c>）：文档 1 req/s。</summary>
    Link,

    /// <summary>其余全部请求（token 刷新、创建/移动/删除/上传等）：文档 15 req/s。</summary>
    Other,
}

/// <summary>
/// 按阿里云**用户**共享的请求限流注册表，对齐 Go <c>limiter.go</c> 的
/// <c>limiters</c> map + <c>getLimiterForUser</c> / <c>free</c>。
/// <para>
/// 配额是「每用户每应用」维度，而非每个挂载点维度 —— 同一账号挂载多次（或配置了引用驱动）
/// 时必须共用同一个限流器，否则总请求速率会成倍超出配额。因此注册表是进程级单例，
/// 驱动在 <c>InitAsync</c> 里 <see cref="Acquire"/> 一个租约、在释放时归还。
/// </para>
/// <para>
/// 空用户 id（<see cref="GlobalUserId"/>）是一个特殊槽位：驱动拿到 <c>user_id</c> 之前的
/// 首个请求（<c>getDriveInfo</c>）只能落在这里，`Init` 完成后立刻切换到真实用户槽位。
/// </para>
/// </summary>
public static class AliyunLimiterRegistry
{
    /// <summary>列目录配额：文档 4 req/s，取 3.9 留余量（对齐 Go <c>listRateLimit</c>）。</summary>
    public const double ListRatePerSecond = 3.9;

    /// <summary>取链接配额：文档 1 req/s，取 0.9 留余量（对齐 Go <c>linkRateLimit</c>）。</summary>
    public const double LinkRatePerSecond = 0.9;

    /// <summary>其他请求配额：文档 15 req/s，取 14.9 留余量（对齐 Go <c>otherRateLimit</c>）。</summary>
    public const double OtherRatePerSecond = 14.9;

    /// <summary>
    /// 全局槽位键（对齐 Go <c>globalLimiterUserID</c>）：用于尚未取得 <c>user_id</c> 的初始请求，
    /// 该槽位不会被回收。
    /// </summary>
    public const string GlobalUserId = "";

    private static readonly ConcurrentDictionary<string, AliyunLimiter> ByUser = new();
    private static readonly object Gate = new();

    /// <summary>
    /// 取得指定用户的限流器租约（引用计数 +1）。同一用户重复调用返回**同一个**限流器实例，
    /// 从而共享三档配额；租约释放后若该用户已无任何引用则回收条目（全局槽位除外）。
    /// </summary>
    /// <param name="userId">阿里云 user_id；传 <see cref="GlobalUserId"/> 表示全局槽位。</param>
    /// <returns>必须在驱动释放时归还的租约。</returns>
    public static AliyunLimiterLease Acquire(string userId)
    {
        // 与 Go 的 limitersLock + usedBy 语义一致：取用/计数在同一临界区内完成，
        // 避免「刚判定为空被清理」与「同时有人取用」交错。
        lock (Gate)
        {
            if (!ByUser.TryGetValue(userId, out var limiter))
            {
                limiter = new AliyunLimiter(userId);
                ByUser[userId] = limiter;
            }
            limiter.UsedBy++;
            return new AliyunLimiterLease(limiter);
        }
    }

    /// <summary>归还租约：引用计数 -1，归零且非全局槽位时从注册表移除。</summary>
    internal static void Release(AliyunLimiter limiter)
    {
        lock (Gate)
        {
            limiter.UsedBy--;
            // Go 在下次 getLimiterForUser 时才清理；这里在归零时立即回收，效果等价且更省内存。
            if (limiter.UsedBy <= 0 && !limiter.IsGlobal)
                ByUser.TryRemove(limiter.UserId, out _);
        }
    }

    /// <summary>注册表当前是否存在该用户的条目（测试可观测性）。</summary>
    internal static bool HasUser(string userId) => ByUser.ContainsKey(userId);

    /// <summary>注册表当前条目数（测试可观测性）。</summary>
    internal static int Count => ByUser.Count;
}

/// <summary>
/// 单个阿里云用户的三档限流器。对齐 Go <c>limiter</c> 结构体的
/// <c>list</c> / <c>link</c> / <c>other</c> 三个 <c>rate.Limiter</c>。
/// </summary>
public sealed class AliyunLimiter
{
    private readonly RequestRateLimiter _list;
    private readonly RequestRateLimiter _link;
    private readonly RequestRateLimiter _other;

    internal AliyunLimiter(string userId)
    {
        UserId = userId;
        // 容量 1、稳态速率按文档留余量 —— 与 Go rate.NewLimiter(rate.Limit(x), 1) 一致。
        _list = new RequestRateLimiter(AliyunLimiterRegistry.ListRatePerSecond);
        _link = new RequestRateLimiter(AliyunLimiterRegistry.LinkRatePerSecond);
        _other = new RequestRateLimiter(AliyunLimiterRegistry.OtherRatePerSecond);
    }

    /// <summary>所属用户（全局槽位为空字符串）。</summary>
    public string UserId { get; }

    /// <summary>是否为全局槽位（不参与回收）。</summary>
    internal bool IsGlobal => UserId.Length == 0;

    /// <summary>当前引用计数（仅注册表在锁内读写）。</summary>
    internal int UsedBy { get; set; }

    /// <summary>按请求类别等待限流放行。</summary>
    /// <param name="kind">请求类别。</param>
    /// <param name="ct">取消令牌。</param>
    public Task WaitAsync(AliyunLimiterKind kind, CancellationToken ct = default) => kind switch
    {
        AliyunLimiterKind.List => _list.WaitAsync(ct),
        AliyunLimiterKind.Link => _link.WaitAsync(ct),
        AliyunLimiterKind.Other => _other.WaitAsync(ct),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown Aliyun limiter kind"),
    };
}

/// <summary>
/// 限流器租约：持有期间可发起限流等待，<see cref="Dispose"/> 时归还引用（幂等）。
/// 对应 Go 的 <c>d.limiter</c> 字段 —— 驱动 Drop 时调用 <c>limiter.free()</c>。
/// </summary>
public sealed class AliyunLimiterLease : IDisposable
{
    private AliyunLimiter? _limiter;

    internal AliyunLimiterLease(AliyunLimiter limiter) => _limiter = limiter;

    /// <summary>所属用户 id。</summary>
    public string UserId => _limiter?.UserId ?? string.Empty;

    /// <summary>按类别等待限流放行。</summary>
    /// <param name="kind">请求类别。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="ObjectDisposedException">租约已归还时抛出（对齐 Go <c>driver not init</c>）。</exception>
    public Task WaitAsync(AliyunLimiterKind kind, CancellationToken ct = default)
    {
        var limiter = Volatile.Read(ref _limiter)
            ?? throw new ObjectDisposedException(nameof(AliyunLimiterLease), "limiter lease already released");
        return limiter.WaitAsync(kind, ct);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var limiter = Interlocked.Exchange(ref _limiter, null);
        if (limiter is not null) AliyunLimiterRegistry.Release(limiter);
    }
}
