namespace OpenList.Drivers.Cloud;

/// <summary>
/// 云驱动的请求速率限制器，语义对齐 Go 的 <c>rate.NewLimiter(rate.Limit(rps), 1)</c>
/// —— 令牌桶容量为 1：首个请求立即放行，之后每个请求需等待 <c>1/rps</c> 秒。
/// <para>
/// 云盘服务普遍对「每用户每应用」的 API 调用频率设限（阿里云盘：list 4/s、link 1/s、
/// 其他 15/s），因此驱动需要在发请求前先等待。多个并发请求会按到达顺序排队，
/// 不会同时放行导致超限。
/// </para>
/// </summary>
public sealed class RequestRateLimiter
{
    private readonly double _intervalSeconds;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;

    /// <summary>
    /// 按「每秒允许的请求数」创建限流器。
    /// </summary>
    /// <param name="requestsPerSecond">
    /// 每秒请求数，对应配置字段 <c>limit_rate</c>。小于等于 0 表示不限流
    /// （对齐 Go 驱动里 <c>if LimitRate &gt; 0</c> 的判断）。
    /// </param>
    public RequestRateLimiter(double requestsPerSecond)
    {
        RequestsPerSecond = requestsPerSecond;
        _intervalSeconds = requestsPerSecond > 0 ? 1.0 / requestsPerSecond : 0;
    }

    /// <summary>本限流器配置的每秒请求数。</summary>
    public double RequestsPerSecond { get; }

    /// <summary>是否处于不限流状态（<see cref="RequestsPerSecond"/> ≤ 0）。</summary>
    public bool IsUnlimited => _intervalSeconds <= 0;

    /// <summary>
    /// 等待直到允许发起下一个请求。
    /// </summary>
    /// <param name="ct">取消令牌；取消时抛出 <see cref="OperationCanceledException"/>。</param>
    /// <returns>表示等待完成的任务。</returns>
    public async Task WaitAsync(CancellationToken ct = default)
    {
        if (IsUnlimited) return;

        // 入口先检查取消：令牌已取消时立即抛出，而不是碰巧拿到空闲时隙后静默成功
        //（对齐 Go limiter.Wait(ctx) 的语义）。
        ct.ThrowIfCancellationRequested();

        TimeSpan delay;
        var interval = TimeSpan.FromSeconds(_intervalSeconds);

        // 在锁内只做「预约下一个放行时刻」的算术，真正的等待放在锁外，
        // 这样并发调用能各自拿到不同的时间片并按序排队。
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (_nextAllowed <= now)
            {
                // 空闲足够久：立即放行，并把下一个时隙排在 interval 之后。
                _nextAllowed = now + interval;
                delay = TimeSpan.Zero;
            }
            else
            {
                delay = _nextAllowed - now;
                _nextAllowed += interval;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct).ConfigureAwait(false);
    }
}
