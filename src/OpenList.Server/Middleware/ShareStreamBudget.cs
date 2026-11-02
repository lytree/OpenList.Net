using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Interfaces;

namespace OpenList.Server.Middleware;

/// <summary>
/// 公开分享的**并发流预算**（单例）：同一个分享码同时只能有 N 条流在传输。
/// <para>
/// 为什么需要它：<c>/sd</c>、<c>/sae</c> 是匿名入口，分享码本身就是能力凭证。
/// 带宽限速（<see cref="RateLimitManager"/>）约束的是「总量」，对「一条链接被几十个
/// 播放器/下载器同时拉」这种情况无效 —— 每个连接都合法、都慢慢流，结果是把整条出口
/// 占满。并发预算把上限钉在单条分享上：超出就 429 + <c>Retry-After</c>，
/// 正常播放器会自动退避重试，滥用方拿不到额外带宽。
/// </para>
/// <para>
/// 上限来自设置项 <see cref="SettingKey"/>（0 或负数 = 不限，默认 0 保持既有行为）。
/// 与限速器一致地每 10 秒热刷新一次，管理员改完无需重启。
/// </para>
/// </summary>
public sealed class ShareStreamBudget
{
    /// <summary>并发上限设置键（Group 11 TRAFFIC）。</summary>
    public const string SettingKey = "share_stream_max_concurrency";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopes;
    private readonly object _refreshLock = new();

    /// <summary>
    /// 分享码 → 在途计数。条目在计数归零后**不删除**：删除需要额外同步才能避免
    /// 「刚被回收又被并发取到」的孤儿计数（导致预算漏算）。分享码由管理员创建、
    /// 数量有限，每条只是一个小对象，保留它们比引入这个竞态更划算。
    /// </summary>
    private readonly ConcurrentDictionary<string, Counter> _counters = new(StringComparer.Ordinal);

    private long _lastRefreshTicks;
    private bool _initialized;
    private volatile int _max;

    public ShareStreamBudget(IServiceScopeFactory scopes)
    {
        _scopes = scopes;
    }

    /// <summary>当前生效的并发上限；0 表示不限。</summary>
    public int Max => _max;

    /// <summary>某个分享当前的在途流数量（诊断与测试用）。</summary>
    public int InFlight(string shareId)
        => _counters.TryGetValue(shareId, out var c) ? c.InFlight : 0;

    /// <summary>
    /// 尝试为 <paramref name="shareId"/> 占用一个并发额度。
    /// 成功返回租约（<see cref="IDisposable.Dispose"/> 即归还）；超限返回 <c>null</c>，
    /// 调用方应回 429。不限速配置下返回不自增的占位租约。
    /// </summary>
    public async Task<IDisposable?> TryAcquireAsync(string shareId, CancellationToken ct = default)
    {
        await EnsureFreshAsync(ct);

        var max = _max;
        if (max <= 0) return NoopLease.Instance;

        var counter = _counters.GetOrAdd(shareId, static _ => new Counter());
        lock (counter)
        {
            if (counter.InFlight >= max) return null;
            counter.InFlight++;
        }
        return new Lease(counter);
    }

    /// <summary>
    /// 确保上限已从设置读取，并周期性刷新（每 <see cref="RefreshInterval"/> 一次）。
    /// 读取失败时维持当前值，绝不抛出。
    /// </summary>
    public async Task EnsureFreshAsync(CancellationToken ct = default)
    {
        bool need;
        lock (_refreshLock)
        {
            var now = Environment.TickCount64;
            need = !_initialized || now - _lastRefreshTicks >= (long)RefreshInterval.TotalMilliseconds;
            if (need)
            {
                // 先置位再读库：并发请求不应排队等一次 DB 往返，读到旧值最多 10 秒。
                _initialized = true;
                _lastRefreshTicks = now;
            }
        }
        if (!need) return;

        try
        {
            using var scope = _scopes.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<ISettingService>();
            var r = await svc.GetAsync(SettingKey, ct);
            _max = r.Code == 200 && r.Data is not null && int.TryParse(r.Data.Value, out var n) && n > 0 ? n : 0;
        }
        catch
        {
            // 设置读取失败维持当前值。
        }
    }

    /// <summary>在途计数（锁内读写）。</summary>
    private sealed class Counter
    {
        public int InFlight;
    }

    /// <summary>额度租约：释放时归还一个名额。重复 Dispose 无副作用。</summary>
    private sealed class Lease : IDisposable
    {
        private Counter? _counter;

        public Lease(Counter counter) => _counter = counter;

        public void Dispose()
        {
            var counter = Interlocked.Exchange(ref _counter, null);
            if (counter is null) return;
            lock (counter)
            {
                if (counter.InFlight > 0) counter.InFlight--;
            }
        }
    }

    /// <summary>不限并发时的占位租约。</summary>
    private sealed class NoopLease : IDisposable
    {
        public static readonly NoopLease Instance = new();

        public void Dispose()
        {
        }
    }
}
