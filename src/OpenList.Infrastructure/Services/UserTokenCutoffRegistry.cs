namespace OpenList.Infrastructure.Services;

/// <summary>
/// 进程级「用户 token 失效截止点」登记表，用来支撑「无状态 JWT 快速路径 + 凭据变更即时失效」的组合方案。
/// <para>
/// 要解决的问题：<see cref="JwtService.IssueToken"/> 把 <c>base_path</c>/<c>permission</c> 签进 token 之后，
/// token 就是一份「签发时刻的权限快照」。管理员事后把某用户的 BasePath 或权限位改小，服务端却无法枚举
/// 该用户已签发的 token 去逐个吊销 —— 旧 token 会带着旧值继续用到过期。
/// Go 没有这个问题（它每请求从库里取用户，改完立刻生效）；.NET 侧刻意保持无状态（不每请求查库），
/// 于是用「记一个截止时间点」兜底：早于该点签发的 token 一律失效，客户端被迫重新登录换取带新值的 token。
/// </para>
/// <para>
/// 局限（与 <see cref="JwtService"/> 既有的内存吊销集合同性质）：登记表在进程内存中，重启即清空；
/// 多实例部署时各实例各自独立。要做到强一致需要换成分布式缓存。
/// </para>
/// </summary>
public sealed class UserTokenCutoffRegistry
{
    private readonly Dictionary<long, DateTime> _cutoffs = new();
    private readonly object _gate = new();

    /// <summary>
    /// 登记「<paramref name="userId"/> 在 <paramref name="at"/> 之前签发的 token 全部失效」。
    /// <para>
    /// 截止点取整秒后 <b>+1 秒</b>：JWT 的 <c>nbf</c> 只有秒级精度，若直接用带小数的时间点，
    /// 变更发生那一秒内刚签发的新 token（<c>nbf</c> 被截断到该秒）会被误判为过期。
    /// 往「多失效一秒」的方向取整是刻意的 fail-closed 选择 —— 代价至多是变更瞬间登录的用户被要求重新登录，
    /// 而不是带着旧权限的 token 被放行。
    /// </para>
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="at">变更发生的时刻（UTC）。</param>
    public void InvalidateUser(long userId, DateTimeOffset at)
    {
        var nextSecond = (at.UtcDateTime.Ticks / TimeSpan.TicksPerSecond + 1) * TimeSpan.TicksPerSecond;
        var cutoff = new DateTime(nextSecond, DateTimeKind.Utc);
        lock (_gate) { _cutoffs[userId] = cutoff; }
    }

    /// <summary>判断该用户在 <paramref name="issuedAtUtc"/> 签发的 token 是否已被登记为失效。</summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="issuedAtUtc">token 的签发生效时间（<c>nbf</c>，即 <c>SecurityToken.ValidFrom</c>）。</param>
    public bool IsStale(long userId, DateTime issuedAtUtc)
    {
        lock (_gate)
            return _cutoffs.TryGetValue(userId, out var cutoff)
                && issuedAtUtc.ToUniversalTime() < cutoff;
    }
}
