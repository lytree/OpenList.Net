using OpenList.Core.Drivers.Base;

namespace OpenList.Drivers.Cloud;

/// <summary>
/// 云盘类驱动（阿里云盘、115、夸克、OneDrive、Google Drive…）的公共基类。
/// <para>
/// 把云驱动反复出现的三件事收敛到这里：共享 <see cref="HttpClient"/>、
/// 基于 <c>limit_rate</c> 的请求限流、以及 OAuth token 刷新后回写 <c>Addition</c>。
/// 具体驱动只需实现协议细节（配置字段、列举、下载链接、上传）。
/// </para>
/// <para>
/// 依赖 <c>DriverBase.MountPath</c> / <c>AdditionSink</c>（由挂载服务注入）来回写配置，
/// 因此 token 持久化只在正式挂载的实例上生效；单元测试直接构造的实例会静默跳过写入。
/// </para>
/// </summary>
public abstract class CloudDriverBase : DriverBase
{
    /// <summary>云驱动需要网络访问。</summary>
    public override bool IsCloudDriver => true;

    /// <summary>共享 HTTP 客户端（进程级单例，勿释放）。</summary>
    protected static HttpClient Http => CloudHttpClient.Shared;

    /// <summary>
    /// 请求限流器。默认不限流；驱动在 <c>InitAsync</c> 里读取 <c>limit_rate</c> 后
    /// 调用 <see cref="ConfigureRateLimit"/> 覆盖。
    /// </summary>
    protected RequestRateLimiter RateLimiter { get; private set; } = new(0);

    /// <summary>
    /// 按每秒请求数配置限流。传 0 或负数表示不限流
    /// （对齐 Go 驱动里 <c>if LimitRate &gt; 0</c> 的判断）。
    /// </summary>
    /// <param name="requestsPerSecond">每秒允许的请求数。</param>
    protected void ConfigureRateLimit(double requestsPerSecond)
        => RateLimiter = new RequestRateLimiter(requestsPerSecond);

    /// <summary>发请求前等待限流放行。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>表示等待完成的任务。</returns>
    protected Task WaitRateLimitAsync(CancellationToken ct = default) => RateLimiter.WaitAsync(ct);

    /// <summary>
    /// 驱动附加配置对象（Addition 实例）。供基类按运行时类型序列化回写数据库。
    /// </summary>
    protected abstract object AdditionObject { get; }

    /// <summary>
    /// token 刷新失败时的总尝试次数（含首次）。默认 4，对齐 Go 的「1 次 + 3 次重试」。
    /// </summary>
    protected virtual int TokenRefreshMaxAttempts => 4;

    /// <summary>
    /// 可选：构造刷新后的令牌持久化过程中的日志回调。
    /// </summary>
    protected virtual Action<Exception, int>? OnTokenRefreshFailure => null;

    /// <summary>
    /// 执行一次令牌刷新（发请求 + 解析响应）。OAuth 类驱动必须实现；
    /// 使用 Cookie 等非 OAuth 认证的云驱动可保持默认实现。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>刷新得到的令牌对。</returns>
    /// <exception cref="NotSupportedException">驱动未实现 OAuth 刷新时抛出。</exception>
    protected virtual Task<OAuthTokens> RefreshTokenOnceAsync(CancellationToken ct)
        => throw new NotSupportedException($"{DriverName} does not implement OAuth token refresh.");

    /// <summary>
    /// 把刷新得到的新令牌写入驱动内存状态（即更新 Addition 上的 token 字段）。
    /// </summary>
    /// <param name="tokens">新令牌。</param>
    /// <exception cref="NotSupportedException">驱动未实现 OAuth 刷新时抛出。</exception>
    protected virtual void ApplyTokens(OAuthTokens tokens)
        => throw new NotSupportedException($"{DriverName} does not implement OAuth token refresh.");

    /// <summary>
    /// 刷新令牌 → 应用到驱动内存 → 回写数据库。
    /// <para>
    /// 顺序很重要：先 <see cref="ApplyTokens"/> 再持久化，保证即使写库失败，
    /// 当前进程内也已经在用新令牌，不会因为一次写库异常就打挂整个请求。
    /// </para>
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>表示刷新与持久化完成的任务。</returns>
    protected async Task RefreshAndPersistTokensAsync(CancellationToken ct = default)
    {
        var refresher = new OAuthTokenRefresher(RefreshTokenOnceAsync)
        {
            MaxAttempts = TokenRefreshMaxAttempts,
            OnFailure = OnTokenRefreshFailure,
        };

        var tokens = await refresher.RefreshAsync(ct).ConfigureAwait(false);
        ApplyTokens(tokens);
        await SaveAdditionAsync(AdditionObject, ct).ConfigureAwait(false);
    }
}
