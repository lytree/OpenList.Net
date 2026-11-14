using System.IdentityModel.Tokens.Jwt;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 进程级「已吊销 token」登记表（登出用）。
/// <para>
/// 这份集合原先挂在 <see cref="JwtService"/> 的实例字段上，而 <c>JwtService</c> 注册为 Scoped ——
/// 登出请求结束时集合随作用域一起丢弃，下一个请求拿到的是新实例、空集合，于是 HTTP 侧登出
/// 实际上从未生效（token 仍能用到过期）。改成单例后，登出才对整个进程可见。
/// </para>
/// <para>
/// 条目带上 token 自身的过期时刻，写入时顺手清理已过期项，避免集合无限增长
/// （过期的 token 本就通不过签名/有效期校验，留着只是占内存）。
/// 局限与 <see cref="UserTokenCutoffRegistry"/> 相同：进程内存，重启清空、多实例各自独立。
/// </para>
/// </summary>
public sealed class RevokedTokenRegistry
{
    private readonly Dictionary<string, DateTime> _revoked = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>
    /// 登记一个已吊销的 token，并清理已过期的历史条目。
    /// </summary>
    /// <param name="token">原始 JWT 字符串。</param>
    /// <param name="expiresAtUtc">该 token 的过期时刻（UTC）；过期后条目可安全丢弃。</param>
    public void Revoke(string token, DateTime expiresAtUtc)
    {
        lock (_gate)
        {
            Prune(DateTime.UtcNow);
            _revoked[token] = expiresAtUtc;
        }
    }

    /// <summary>判断 token 是否已被吊销。</summary>
    /// <param name="token">原始 JWT 字符串。</param>
    public bool IsRevoked(string token)
    {
        lock (_gate) return _revoked.ContainsKey(token);
    }

    /// <summary>
    /// 从 token 中读出过期时刻；解析失败时回退到「当前时间 + 默认有效期」，
    /// 宁可多留一会儿，也不要让无法解析的 token 逃过吊销。
    /// </summary>
    /// <param name="token">原始 JWT 字符串。</param>
    /// <param name="fallbackLifetime">解析失败时使用的保守有效期。</param>
    internal static DateTime ResolveExpiry(string token, TimeSpan fallbackLifetime)
    {
        try
        {
            return new JwtSecurityTokenHandler().ReadJwtToken(token).ValidTo;
        }
        catch
        {
            return DateTime.UtcNow.Add(fallbackLifetime);
        }
    }

    /// <summary>清理已过期条目（调用方需持有 <see cref="_gate"/>）。</summary>
    private void Prune(DateTime nowUtc)
    {
        if (_revoked.Count == 0) return;
        List<string>? expired = null;
        foreach (var (token, expiresAt) in _revoked)
        {
            if (expiresAt <= nowUtc) (expired ??= []).Add(token);
        }
        if (expired is null) return;
        foreach (var token in expired) _revoked.Remove(token);
    }
}
