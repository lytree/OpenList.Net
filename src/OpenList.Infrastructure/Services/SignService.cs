using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 用于 /d/、/p/、/ad/、/ap/、/ae/ 等路由的 HMAC-SHA256 签名 URL 生成/校验器。
/// 对应 Go 参考实现 <c>pkg/sign/hmac.go</c> + <c>internal/sign/sign.go</c>。
/// 签名格式：<c>base64url(hmac_sha256(path + ":" + expire)) + ":" + expire</c>。
/// expire=0 表示永不过期；密钥为配置中设定的 JWT 密钥。
/// </summary>
public sealed class SignService : ISignService
{
    private readonly byte[] _secret;

    /// <summary>
    /// 创建签名服务实例，读取配置中的 JWT 密钥作为 HMAC 密钥。
    /// </summary>
    /// <param name="config">应用配置（含 JwtSecret）。</param>
    public SignService(IOptions<AppConfiguration> config)
    {
        _secret = Encoding.UTF8.GetBytes(config.Value.JwtSecret ?? string.Empty);
    }

    /// <summary>
    /// 为指定路径生成签名（可指定过期时间戳，缺省为 0 即永不过期）。
    /// </summary>
    /// <param name="path">被签名的路径。</param>
    /// <param name="expiration">Unix 秒级过期时间，0 表示永不过期。</param>
    /// <returns>形如 <c>base64url(hmac) + ":" + expire</c> 的签名串。</returns>
    public string Sign(string path, long? expiration = null)
    {
        var expire = expiration ?? 0;
        var expireStr = expire.ToString();
        using var hmac = new HMACSHA256(_secret);
        var payload = Encoding.UTF8.GetBytes(path + ":" + expireStr);
        var hash = hmac.ComputeHash(payload);
        return Base64UrlEncode(hash) + ":" + expireStr;
    }

    /// <summary>
    /// 校验签名：解析过期时间、检查是否过期，并用常量时间比较重算的期望签名。
    /// </summary>
    /// <param name="path">被签名的路径。</param>
    /// <param name="sign">待校验的签名串。</param>
    /// <returns>签名有效且未过期时为 true。</returns>
    public bool Verify(string path, string sign)
    {
        if (string.IsNullOrEmpty(sign)) return false;
        var colon = sign.LastIndexOf(':');
        if (colon < 0) return false;
        var expireStr = sign[(colon + 1)..];
        if (!long.TryParse(expireStr, out var expire)) return false;
        // expire=0 表示永不过期；否则判断是否已失效。
        if (expire != 0 && expire < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;
        // 常量时间比较，避免时序侧信道。
        var expected = Sign(path, expire);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(sign));
    }

    /// <summary>
    /// 将字节数组编码为 URL 安全的 Base64（去掉填充，/ 与 + 替换为 _ 与 -）。
    /// </summary>
    private static string Base64UrlEncode(byte[] bytes)
    {
        // 与 Go 的 base64.URLEncoding 一致（含填充），为 URL 安全去掉末尾填充。
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
