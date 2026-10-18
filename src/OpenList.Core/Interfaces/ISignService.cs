namespace OpenList.Core.Interfaces;

/// <summary>
/// 一次签名判定的结果：要不要签，以及签名该带上什么过期时间。
/// <para>
/// 对齐 Go <c>server/common/sign.go</c> 的 <c>Sign(obj, parent, encrypt)</c> 与
/// <c>server/middlewares/down.go</c> 的 <c>needSign</c> —— 两侧必须用<b>同一个</b>判定，
/// 否则会出现「发了签名但不校验」或「校验了却没发签名」的错位。
/// </para>
/// </summary>
/// <param name="NeedSign">该路径是否需要签名（<c>sign_all</c> 或所属存储 <c>enable_sign</c>）。</param>
/// <param name="ExpireAt">签名携带的过期时间戳（Unix 秒），0 表示永不过期。</param>
public readonly record struct SignContext(bool NeedSign, long ExpireAt)
{
    /// <summary>无需签名（默认配置下的普通文件）。</summary>
    public static readonly SignContext NotNeeded = new(false, 0);
}

/// <summary>
/// 签名服务：为 /d/、/p/、/ad/、/ap/、/ae/ 路由生成与校验签名 URL。
/// 与 OpenList Go 的 <c>server/common/sign.go</c> + <c>internal/sign/sign.go</c> +
/// <c>server/middlewares/down.go</c> 对应。
/// </summary>
public interface ISignService
{
    /// <summary>
    /// 解析某路径的签名策略：是否需要签名、签名应带什么过期时间。
    /// 对应 Go 的 <c>needSign</c>（<c>sign_all</c> || 所属存储的 <c>enable_sign</c>）。
    /// </summary>
    /// <param name="path">完整挂载路径（如 <c>/s3/movie.mkv</c>）。</param>
    /// <param name="ct">取消令牌。</param>
    Task<SignContext> GetContextAsync(string path, CancellationToken ct = default);

    /// <summary>为给定路径生成签名；<paramref name="expireAt"/> 为过期时间戳（0 = 永不过期）。</summary>
    string Sign(string path, long expireAt = 0);

    /// <summary>校验路径与签名的有效性（含过期判断）。</summary>
    bool Verify(string path, string sign);
}
