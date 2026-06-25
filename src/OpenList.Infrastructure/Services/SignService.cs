using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// HMAC-SHA256 signed URL generator/verifier for /d/, /p/, /ad/, /ap/, /ae/ routes.
/// Mirrors OpenList Go <c>pkg/sign/hmac.go</c> + <c>internal/sign/sign.go</c>.
/// Signature format: <c>base64url(hmac_sha256(path + ":" + expire)) + ":" + expire</c>.
/// expire=0 means never expires. Secret key is the configured JWT secret.
/// </summary>
public sealed class SignService : ISignService
{
    private readonly byte[] _secret;

    public SignService(IOptions<AppConfiguration> config)
    {
        _secret = Encoding.UTF8.GetBytes(config.Value.JwtSecret ?? string.Empty);
    }

    public string Sign(string path, long? expiration = null)
    {
        var expire = expiration ?? 0;
        var expireStr = expire.ToString();
        using var hmac = new HMACSHA256(_secret);
        var payload = Encoding.UTF8.GetBytes(path + ":" + expireStr);
        var hash = hmac.ComputeHash(payload);
        return Base64UrlEncode(hash) + ":" + expireStr;
    }

    public bool Verify(string path, string sign)
    {
        if (string.IsNullOrEmpty(sign)) return false;
        var colon = sign.LastIndexOf(':');
        if (colon < 0) return false;
        var expireStr = sign[(colon + 1)..];
        if (!long.TryParse(expireStr, out var expire)) return false;
        // expire=0 means never expires; otherwise check against now
        if (expire != 0 && expire < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;
        // Constant-time comparison
        var expected = Sign(path, expire);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(sign));
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        // Matches Go's base64.URLEncoding (with padding). Trim padding for URL safety.
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
