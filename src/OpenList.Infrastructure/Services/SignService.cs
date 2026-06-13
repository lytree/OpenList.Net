using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

public class SignService : ISignService
{
    private readonly byte[] _key;
    private readonly int _linkExpirationHours;

    public SignService(IConfiguration config)
    {
        var secret = config["SignKey"] ?? "openlist-sign-key";
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        _linkExpirationHours = config.GetValue("LinkExpiration", 0); // 0 = never expires
    }

    public string Sign(string path)
    {
        var expire = _linkExpirationHours > 0
            ? DateTimeOffset.UtcNow.AddHours(_linkExpirationHours).ToUnixTimeSeconds()
            : 0L;

        return ComputeSign(path, expire);
    }

    public string SignArchive(string path) => Sign("archive:" + path);

    public bool Verify(string sign, string path)
    {
        if (string.IsNullOrEmpty(sign)) return false;
        try
        {
            // Format: base64url(hmac):expireTimestamp
            var colonIndex = sign.LastIndexOf(':');
            if (colonIndex < 0) return false;

            var expireStr = sign[(colonIndex + 1)..];
            if (!long.TryParse(expireStr, out var expire)) return false;

            // Check expiration (0 = never expires)
            if (expire != 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expire) return false;

            // Verify signature
            var expected = ComputeSign(path, expire);
            return sign == expected;
        }
        catch { return false; }
    }

    public bool VerifyArchive(string sign, string path) => Verify(sign, "archive:" + path);

    private string ComputeSign(string data, long expire)
    {
        var payload = $"{data}:{expire}";
        using var hmac = new HMACSHA256(_key);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_').TrimEnd('=') + ":" + expire;
    }
}


