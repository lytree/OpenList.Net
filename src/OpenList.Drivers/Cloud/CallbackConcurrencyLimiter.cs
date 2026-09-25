using System.Collections.Concurrent;

namespace OpenList.Drivers.Cloud;

/// <summary>
/// 回调准入的「临时容量不足」异常，对齐 Go 的 <c>errs.TemporaryCapacity</c>。
/// <para>
/// 语义：这不是请求本身失败，而是阿里云盘回调通道当前没有空闲容量
/// （本地等待超时，或连续多次被 <c>RequestDeniedByCallback/ExceedMaxConcurrency</c> 拒绝）。
/// 上层（未来的 S3 网关、代理下载端点）可据此返回 503 SlowDown 类响应，而不是 500。
/// </para>
/// </summary>
public sealed class CallbackCapacityException : Exception
{
    /// <summary>用指定的错误消息构造异常。</summary>
    public CallbackCapacityException(string message) : base(message) { }
}

/// <summary>
/// 阿里云盘代理下载回调的并发准入注册表（进程级、按用户共享）。
/// <para>
/// 对齐 Go <c>drivers/aliyundrive_open/callback.go</c> 的 <c>callbackLimiters</c> 包级变量：
/// 同一阿里云用户（user_id）的所有存储共享一个准入配额 —— 有效上限取该用户名下
/// 所有注册中最小的 <c>CallbackConcurrency</c>；任一存储注销后自动收敛，
/// 用户名下全部注销且无活动许可时回收该用户的限流器。
/// </para>
/// <para>
/// 许可（<see cref="CallbackPermit"/>）必须覆盖整个响应体的生命周期：
/// 从发起回调请求前获取，到响应流被释放（读完 / 中途取消 / 显式关闭）才归还，
/// 而不是只在 HTTP 响应头返回时归还 —— 这与 Go 的 <c>newCallbackBody</c> 一致。
/// </para>
/// </summary>
public static class CallbackLimiterRegistry
{
    /// <summary>默认回调并发上限，对齐 Go <c>defaultCallbackConcurrency</c>。</summary>
    public const int DefaultCallbackConcurrency = 1;

    /// <summary>等待准入的超时时间。超过即抛 <see cref="CallbackCapacityException"/>。
    /// internal 可写仅用于测试把等待时间调短。</summary>
    internal static TimeSpan AcquireTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>重试等待准入的基础退避，对齐 Go <c>callbackRetryBaseDelay</c>。</summary>
    internal static TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>回调请求的最大尝试次数（含首次），对齐 Go <c>callbackRequestAttempts</c>。</summary>
    internal const int RequestAttempts = 3;

    // registryLock → limiter._gate 的加锁顺序全工程一致（与 Go 先 callbackLimiters 再 mu 对应）。
    private static readonly ConcurrentDictionary<string, CallbackLimiter> ByUser = new(StringComparer.Ordinal);

    /// <summary>归一化并发配置：≤0 回落默认值，对齐 Go <c>normalizeCallbackConcurrency</c>。</summary>
    public static int Normalize(int limit)
        => limit <= 0 ? DefaultCallbackConcurrency : limit;

    /// <summary>
    /// 为指定阿里云用户注册一个回调准入名额。同一用户重复注册会合并：
    /// 有效上限是所有注册值的最小值（<see cref="CallbackLimiter.EffectiveLimit"/>）。
    /// 返回的句柄在驱动 Drop/释放时必须调用 <see cref="CallbackLimiterRegistration.Unregister"/>。
    /// </summary>
    /// <param name="userId">阿里云 user_id（共享配额的身份键）。</param>
    /// <param name="limit">本存储声明的并发上限（≤0 归一化为默认值）。</param>
    public static CallbackLimiterRegistration Register(string userId, int limit)
    {
        var limiter = ByUser.GetOrAdd(userId, static id => new CallbackLimiter(id));
        var id = limiter.AddRegistration(Normalize(limit));
        return new CallbackLimiterRegistration(limiter, id);
    }

    /// <summary>该用户当前是否仍保有注册（测试用：验证回收语义）。</summary>
    internal static bool HasUser(string userId) => ByUser.ContainsKey(userId);

    internal static void RemoveIfIdle(CallbackLimiter limiter)
    {
        // 仅在确认「无注册且无活动许可」后移除，避免把还在用的限流器删掉。
        lock (limiter.Gate)
        {
            if (limiter.Registrations.Count != 0 || limiter.Active != 0) return;
            ByUser.TryRemove(limiter.UserId, out _);
        }
    }
}

/// <summary>
/// 单个阿里云用户的回调并发限流器。并发模型：
/// 活动许可数（<see cref="CallbackLimiter.Active"/>）与各存储注册的声明上限取最小值比较，
/// 空闲时立即放行；满员时等待信号（放行/注册变化）直到超时。
/// </summary>
internal sealed class CallbackLimiter
{
    /// <summary>等待者的唤醒信号：发信号即完成当前 TCS 并换新的，语义同 Go 的 close(changed)。</summary>
    private TaskCompletionSource<bool>? _changed;

    internal CallbackLimiter(string userId) => UserId = userId;

    internal string UserId { get; }
    internal object Gate { get; } = new();
    internal Dictionary<ulong, int> Registrations { get; } = [];
    internal int Active { get; set; }
    private ulong _nextId;

    /// <summary>当前有效并发上限：所有注册值的最小值；无注册时为 0（无法准入）。</summary>
    internal int EffectiveLimit
    {
        get
        {
            lock (Gate)
            {
                return EffectiveLimitLocked();
            }
        }
    }

    internal int EffectiveLimitLocked()
    {
        var limit = 0;
        foreach (var registered in Registrations.Values)
        {
            if (limit == 0 || registered < limit) limit = registered;
        }
        return limit;
    }

    /// <summary>登记一个注册名额并唤醒等待者（上限可能变大），返回注册 id。</summary>
    internal ulong AddRegistration(int limit)
    {
        ulong id;
        lock (Gate)
        {
            id = ++_nextId;
            Registrations[id] = limit;
            SignalLocked();
        }
        return id;
    }

    /// <summary>移除注册并唤醒等待者。对齐 Go <c>callbackRegistration.unregister</c>。</summary>
    internal void RemoveRegistration(ulong id)
    {
        lock (Gate)
        {
            Registrations.Remove(id);
            SignalLocked();
        }
    }

    /// <summary>在锁内广播一次状态变化（放行、注册增减都会改变可准入性）。</summary>
    internal void SignalLocked()
    {
        _changed?.TrySetResult(true);
        _changed = null;
    }

    /// <summary>
    /// 获取一个回调准入许可。配额满时等待 <see cref="CallbackLimiterRegistry.AcquireTimeout"/>，
    /// 超时抛 <see cref="CallbackCapacityException"/>（对齐 Go 的 TemporaryCapacity）；
    /// 调用方取消则抛 <see cref="OperationCanceledException"/>。
    /// </summary>
    internal async Task<CallbackPermit> AcquireAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(CallbackLimiterRegistry.AcquireTimeout);

        while (true)
        {
            Task waitTask;
            lock (Gate)
            {
                ct.ThrowIfCancellationRequested();
                var limit = EffectiveLimitLocked();
                if (Active < limit)
                {
                    Active++;
                    return new CallbackPermit(this);
                }
                _changed ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                waitTask = _changed.Task;
            }

            try
            {
                await waitTask.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 超时（而非调用方取消）：对齐 Go 的
                // errs.NewErr(errs.TemporaryCapacity, "timed out waiting for callback admission")。
                throw new CallbackCapacityException("timed out waiting for callback admission");
            }
        }
    }

    /// <summary>归还许可并唤醒等待者；用户名下全部空闲时回收限流器。</summary>
    internal void Release()
    {
        lock (Gate)
        {
            Active--;
            SignalLocked();
        }
        CallbackLimiterRegistry.RemoveIfIdle(this);
    }
}

/// <summary>
/// 驱动持有的回调准入注册句柄：Init 时经 <see cref="CallbackLimiterRegistry.Register"/> 创建，
/// Drop 时 <see cref="Unregister"/>。多线程下注销只生效一次（对齐 Go 的 sync.Once）。
/// </summary>
public sealed class CallbackLimiterRegistration : IDisposable
{
    private readonly CallbackLimiter _limiter;
    private readonly ulong _id;
    private int _unregistered;

    internal CallbackLimiterRegistration(CallbackLimiter limiter, ulong id)
    {
        _limiter = limiter;
        _id = id;
    }

    /// <summary>本注册对应的共享限流器（未注册的驱动返回 null 时不可准入）。</summary>
    internal CallbackLimiter Limiter => _limiter;

    /// <summary>获取一个准入许可（覆盖整个响应体生命周期），见 <see cref="CallbackLimiter.AcquireAsync"/>。</summary>
    internal Task<CallbackPermit> AcquireAsync(CancellationToken ct) => _limiter.AcquireAsync(ct);

    /// <summary>注销本注册；此后再获取许可会抛 <see cref="CallbackCapacityException"/>（对齐 Go 的 nil registration）。</summary>
    public void Unregister()
    {
        if (Interlocked.Exchange(ref _unregistered, 1) != 0) return;
        _limiter.RemoveRegistration(_id);
        CallbackLimiterRegistry.RemoveIfIdle(_limiter);
    }

    /// <inheritdoc />
    public void Dispose() => Unregister();
}

/// <summary>
/// 一次回调准入许可：构造即占坑，<see cref="Dispose"/> 归还，且只归还一次
/// （对齐 Go <c>callbackPermit.release</c> 的 sync.Once）。
/// 典型用法是把许可绑定到响应流：<c>using</c> 作用域覆盖流的整个读取过程。
/// </summary>
public sealed class CallbackPermit : IDisposable
{
    private CallbackLimiter? _limiter;
    private int _released;

    internal CallbackPermit(CallbackLimiter limiter) => _limiter = limiter;

    /// <summary>归还许可（幂等）。</summary>
    public void Dispose()
    {
        var limiter = Interlocked.Exchange(ref _limiter, null);
        if (limiter is null || Interlocked.Exchange(ref _released, 1) != 0) return;
        limiter.Release();
    }
}
