using System.Collections.Concurrent;

namespace OpenList.Media.Services;

/// <summary>
/// 整库刮削的运行态协调器（单例）。
///
/// 职责必须跨请求存活，因此**不能**放在 <see cref="MediaMetadataService"/> 里
/// —— 那是 Scoped 的，实例字段每次请求都是新的，守卫形同虚设：
/// <list type="number">
/// <item>并发守卫：同一库同一时刻只允许一个批量刮削。批量任务每条之间 sleep 250ms，
/// 2000 条要跑 8 分钟；没有守卫时双击会让两个后台任务同时搜同一批条目，
/// 白白消耗 provider 配额，还容易撞限流。</item>
/// <item>进度换算：条目状态本身就是进度事实（Pending 随刮削单调减少），
/// 不需要在后台任务里额外累加计数，按「起始待刮削数 − 当前待刮削数」算即可。</item>
/// </list>
///
/// 结束的一轮**保留不删**，只把 <see cref="MediaScrapeRun.Running"/> 置 false：
/// 连续失败熔断时如果连同记录一起扔掉，管理台只会看到「pending 没变、也没在跑」，
/// 完全看不出是被熔断中止了。<see cref="MediaScrapeRun.Error"/> 与
/// <see cref="MediaScrapeRun.Failed"/> 就是给这个场景留的。
///
/// 刻意不落库：进程重启后状态归零是正确的（任务也随进程一起没了）。
/// </summary>
public sealed class MediaScrapeCoordinator
{
    /// <summary>单轮批量刮削最多处理的条目数。与 <c>ScrapeLibraryAsync</c> 的取数上限一致。</summary>
    public const int MaxBatch = 2000;

    /// <summary>连续失败多少条就中止本轮（见 <see cref="MediaScrapeRun.ShouldAbort"/>）。</summary>
    public const int AbortAfterConsecutiveFailures = 5;

    private readonly ConcurrentDictionary<long, MediaScrapeRun> _runs = new();

    /// <summary>
    /// 尝试为指定库登记一轮批量刮削。已有一轮在跑时返回 <c>false</c>。
    ///
    /// 用 CAS 循环而不是「先查后写」：后者两个并发请求会同时通过检查。
    /// 这里也不能直接 <c>TryAdd</c> —— 结束的一轮会留在字典里，<c>TryAdd</c> 会永远失败。
    /// </summary>
    /// <param name="libraryId">媒体库 id。</param>
    /// <param name="startPending">本轮开始时的待刮削条目数（可大于批量上限）。</param>
    /// <returns>登记成功返回 <c>true</c>。</returns>
    public bool TryBegin(long libraryId, int startPending)
    {
        var fresh = new MediaScrapeRun(startPending, Math.Min(startPending, MaxBatch));
        while (true)
        {
            if (!_runs.TryGetValue(libraryId, out var existing))
            {
                if (_runs.TryAdd(libraryId, fresh)) return true;
                continue;
            }
            if (existing.Running) return false;
            // 上一轮已结束：换成新一轮。TryUpdate 带值比较，别的线程抢先换掉了就重来。
            if (_runs.TryUpdate(libraryId, fresh, existing)) return true;
        }
    }

    /// <summary>
    /// 结束一轮刮削（务必放在 <c>finally</c> 里，否则这一库会永远停在「正在刮削」）。
    /// 记录本身保留，让最后一轮的失败原因还能被读到。
    /// </summary>
    /// <param name="libraryId">媒体库 id。</param>
    public void End(long libraryId)
    {
        if (_runs.TryGetValue(libraryId, out var run)) run.Finish();
    }

    /// <summary>取运行态（含上一轮已结束的记录）；该库从未刮过返回 <c>null</c>。</summary>
    /// <param name="libraryId">媒体库 id。</param>
    public MediaScrapeRun? Get(long libraryId) => _runs.TryGetValue(libraryId, out var run) ? run : null;
}

/// <summary>
/// 一轮批量刮削的运行时快照。写入方是后台任务本身，读取方是状态端点，
/// 因此可变字段都走 <see cref="Interlocked"/> / <c>volatile</c>。
/// </summary>
public sealed class MediaScrapeRun
{
    private int _failed;
    private int _consecutiveFailures;
    private string? _error;
    private volatile bool _running = true;

    /// <summary>创建一轮运行态。</summary>
    /// <param name="startPending">开始时待刮削条目数。</param>
    /// <param name="batchSize">本轮计划处理条目数（已按上限截断）。</param>
    public MediaScrapeRun(int startPending, int batchSize)
    {
        StartPending = startPending;
        BatchSize = batchSize;
    }

    /// <summary>本轮开始时的待刮削条目数。</summary>
    public int StartPending { get; }

    /// <summary>本轮计划处理的条目数。</summary>
    public int BatchSize { get; }

    /// <summary>是否仍在运行。</summary>
    public bool Running => _running;

    /// <summary>本轮累计失败条目数。</summary>
    public int Failed => Volatile.Read(ref _failed);

    /// <summary>最近一次失败的错误信息，可为空。</summary>
    public string? Error => _error;

    /// <summary>
    /// 是否应当中止：连续失败达到 <see cref="MediaScrapeCoordinator.AbortAfterConsecutiveFailures"/>。
    /// 存在的理由是「配置错」与「个别条目匹配不到」必须区分开 ——
    /// API key 失效时每条都会失败，若不熔断，整批会在 8 分钟里静默跑完且什么都不写。
    /// </summary>
    public bool ShouldAbort =>
        Volatile.Read(ref _consecutiveFailures) >= MediaScrapeCoordinator.AbortAfterConsecutiveFailures;

    /// <summary>标记本轮结束。</summary>
    public void Finish() => _running = false;

    /// <summary>记录一次成功（重置连续失败计数）。</summary>
    public void RecordSuccess() => Interlocked.Exchange(ref _consecutiveFailures, 0);

    /// <summary>记录一次失败。</summary>
    /// <param name="error">错误信息，会作为最近错误透给管理台。</param>
    public void RecordFailure(string error)
    {
        Interlocked.Increment(ref _failed);
        Interlocked.Increment(ref _consecutiveFailures);
        _error = error;
    }

    /// <summary>
    /// 本轮已处理条目数 = 起始待刮削数 − 当前待刮削数，夹在 <c>[0, BatchSize]</c>。
    /// 夹取不是防御性冗余：运行期间若有扫描插进来新增了条目，差值会变小甚至为负，
    /// 进度条不该因此倒着走或溢出。
    /// </summary>
    /// <param name="pendingNow">当前待刮削条目数。</param>
    public int DoneOf(int pendingNow) => Math.Clamp(StartPending - pendingNow, 0, BatchSize);
}
