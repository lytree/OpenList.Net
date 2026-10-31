using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenList.Core.Interfaces;

namespace OpenList.Infrastructure.Tasks;

/// <summary>
/// 每个任务类型的并发上限快照（0 = 不限）。
/// <para>
/// 对应 Go 参考实现里的 <c>*_task_threads_num</c> 系列设置。这里的并发指
/// 「同一类型下同时处于运行态的任务数」——复制/移动是「一个文件一个任务」，
/// 因此并发数即同时处理的文件数，与 Go 侧语义一致。
/// </para>
/// </summary>
public sealed class TaskConcurrencyLimits
{
    /// <summary>设置键 → 任务类型的映射（顺序即设置页展示顺序）。</summary>
    public static readonly IReadOnlyList<(TaskType Type, string Key)> Keys =
    [
        (TaskType.Copy, "copy_task_threads_num"),
        (TaskType.Upload, "upload_task_threads_num"),
        (TaskType.Download, "download_task_threads_num"),
        (TaskType.Move, "move_task_threads_num"),
        (TaskType.Decompress, "decompress_download_task_threads_num"),
        (TaskType.DecompressUpload, "decompress_upload_task_threads_num"),
        (TaskType.OfflineDownload, "offline_download_task_threads_num"),
        (TaskType.OfflineDownloadTransfer, "offline_download_transfer_task_threads_num"),
    ];

    /// <summary>缺省上限：与 Go 一致取 5。</summary>
    public const int DefaultLimit = 5;

    /// <summary>全不限（未接线时的兜底，行为与接线前完全一致）。</summary>
    public static readonly TaskConcurrencyLimits Unlimited = new(new Dictionary<TaskType, int>());

    private readonly IReadOnlyDictionary<TaskType, int> _limits;

    /// <summary>创建快照。</summary>
    /// <param name="limits">各类型上限；未出现的类型按不限处理。</param>
    public TaskConcurrencyLimits(IReadOnlyDictionary<TaskType, int> limits) => _limits = limits;

    /// <summary>取某类型的并发上限；0 表示不限。</summary>
    public int LimitOf(TaskType type) => _limits.TryGetValue(type, out var v) ? v : 0;

    /// <summary>
    /// 把设置项字典映射成快照。缺键用默认值；非法/负值一律按不限（0）处理 ——
    /// 一个写错的设置项不该让任务直接卡死在等待队列里。
    /// </summary>
    public static TaskConcurrencyLimits FromSettings(IReadOnlyDictionary<string, string> settings)
    {
        var limits = new Dictionary<TaskType, int>(Keys.Count);
        foreach (var (type, key) in Keys)
        {
            limits[type] = settings.TryGetValue(key, out var raw) && int.TryParse(raw, out var parsed)
                ? Math.Max(0, parsed)
                : DefaultLimit;
        }
        return new TaskConcurrencyLimits(limits);
    }
}

/// <summary>
/// 按类型计数的并发闸门，上限可在运行期调整。
/// <para>
/// 不用 <see cref="SemaphoreSlim"/>：它的容量在构造后不可变，而这里的上限来自
/// 设置表（管理员随时改）。若每次改设置就重建信号量，旧实例上的占用会「丢失」，
/// 短暂超出上限；自管计数 + 等待队列可以在改上限时原地唤醒等待者。
/// </para>
/// </summary>
internal sealed class ConcurrencyGate
{
    private readonly object _sync = new();
    private readonly Queue<TaskCompletionSource<bool>> _waiters = new();
    private int _limit;
    private int _inUse;

    public ConcurrencyGate(int limit) => Volatile.Write(ref _limit, Math.Max(0, limit));

    /// <summary>当前上限（0 = 不限）；仅用于变更检测的快速路径，可读陈旧值。</summary>
    public int Limit => Volatile.Read(ref _limit);

    /// <summary>调整上限并立即唤醒可放行的等待者。</summary>
    public void SetLimit(int limit)
    {
        lock (_sync)
        {
            Volatile.Write(ref _limit, Math.Max(0, limit));
            DispatchLocked();
        }
    }

    /// <summary>
    /// 申请一个运行名额；上限内立即返回，否则排队直到有名额或被取消。
    /// 返回的 <see cref="Lease"/> 必须在任务结束时 Dispose，否则名额永久泄漏。
    /// </summary>
    public async ValueTask<Lease> AcquireAsync(CancellationToken ct)
    {
        TaskCompletionSource<bool> waiter;
        lock (_sync)
        {
            if (_limit <= 0 || _inUse < _limit)
            {
                _inUse++;
                return new Lease(this);
            }

            waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(waiter);
        }

        // 取消时只标记 waiter：它可能已在队列中排位，出队时由 DispatchLocked 跳过，
        // 这样不必在锁外做「从队列中间移除」这种 O(n) 且易错的操作。
        using var reg = ct.Register(static state =>
            ((TaskCompletionSource<bool>)state!).TrySetCanceled(), waiter);

        await waiter.Task.ConfigureAwait(false);
        return new Lease(this);
    }

    /// <summary>归还名额并唤醒下一个等待者。</summary>
    internal void Release()
    {
        lock (_sync)
        {
            if (_inUse > 0) _inUse--;
            DispatchLocked();
        }
    }

    /// <summary>把空余名额派发给队列头部仍然有效的等待者。</summary>
    private void DispatchLocked()
    {
        while (_waiters.Count > 0)
        {
            // 不限：全部放行（取消的等待者已由 TrySetResult 的返回值体现为 false）。
            if (_limit > 0 && _inUse >= _limit) return;
            var waiter = _waiters.Dequeue();
            if (waiter.Task.IsCompleted) continue;
            _inUse++;
            // 先占名额再放行；若此刻已被取消取消（TrySetResult 失败），把名额退回去，
            // 否则这个名额没有任何 Lease 持有者，会被永久漏掉。
            if (!waiter.TrySetResult(true)) _inUse--;
        }
    }

    /// <summary>运行名额租约；Dispose 即归还。用 class 而非 struct：struct 被复制会重复归还。</summary>
    internal sealed class Lease : IDisposable
    {
        private ConcurrencyGate? _gate;

        public Lease(ConcurrencyGate gate) => _gate = gate;

        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            gate?.Release();
        }
    }
}

/// <summary>任务并发上限的只读来源。</summary>
public interface ITaskConcurrencyProvider
{
    /// <summary>当前快照（永不为 null）。</summary>
    TaskConcurrencyLimits Current { get; }
}

/// <summary>
/// 设置项 → <see cref="TaskConcurrencyLimits"/> 快照的持有者（单例）。
/// <para>
/// 与 <see cref="Thumbnails.ThumbnailOptionsProvider"/> 同一套路：后台每 20 秒重读一次设置表，
/// 任务创建时只读内存快照，不查库。改设置后最多 20 秒生效，对并发上限这种参数足够。
/// </para>
/// </summary>
public sealed class TaskConcurrencyProvider : ITaskConcurrencyProvider, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(20);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<TaskConcurrencyProvider>? _log;
    private readonly CancellationTokenSource _stop = new();
    private volatile TaskConcurrencyLimits _current = TaskConcurrencyLimits.Unlimited;

    /// <summary>创建提供者并启动后台刷新循环。</summary>
    /// <param name="scopes">作用域工厂（读取 scoped 的 <see cref="ISettingService"/>）。</param>
    /// <param name="log">日志器（可选）。</param>
    public TaskConcurrencyProvider(IServiceScopeFactory scopes, ILogger<TaskConcurrencyProvider>? log = null)
    {
        _scopes = scopes;
        _log = log;
        _ = Task.Run(RefreshLoopAsync);
    }

    /// <inheritdoc />
    public TaskConcurrencyLimits Current => _current;

    /// <summary>立即重读一次设置（供启动流程与测试调用）。</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var settings = scope.ServiceProvider.GetService<ISettingService>();
            if (settings is null) return;
            var resp = await settings.ListAsync(ct).ConfigureAwait(false);
            if (resp.Code != 200 || resp.Data is null) return;
            var dict = resp.Data.ToDictionary(s => s.Key, s => s.Value ?? string.Empty, StringComparer.Ordinal);
            _current = TaskConcurrencyLimits.FromSettings(dict);
        }
        catch (Exception ex)
        {
            // 读不到设置就保留上一份快照：并发上限退回默认值也不会让任务失败。
            _log?.LogDebug(ex, "task concurrency: refresh limits failed");
        }
    }

    private async Task RefreshLoopAsync()
    {
        await RefreshAsync(_stop.Token).ConfigureAwait(false);
        using var timer = new PeriodicTimer(RefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                await RefreshAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* 进程退出 */ }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
