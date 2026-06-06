using System.Security.Cryptography;
using System.Text;

namespace OpenList.Tests;

public class SignServiceTests
{
    // Replicate the core sign logic for unit testing without DI
    private static byte[] DeriveKey(string secret)
        => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    private static string ComputeSign(byte[] key, string data, long expire)
    {
        var payload = $"{data}:{expire}";
        using var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_').TrimEnd('=') + ":" + expire;
    }

    private static string Sign(byte[] key, string path, int expirationHours = 0)
    {
        var expire = expirationHours > 0
            ? DateTimeOffset.UtcNow.AddHours(expirationHours).ToUnixTimeSeconds()
            : 0L;
        return ComputeSign(key, path, expire);
    }

    private static bool Verify(byte[] key, string sign, string path)
    {
        if (string.IsNullOrEmpty(sign)) return false;
        try
        {
            var colonIndex = sign.LastIndexOf(':');
            if (colonIndex < 0) return false;
            var expireStr = sign[(colonIndex + 1)..];
            if (!long.TryParse(expireStr, out var expire)) return false;
            if (expire != 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expire) return false;
            var expected = ComputeSign(key, path, expire);
            return sign == expected;
        }
        catch { return false; }
    }

    [Test]
    public async Task SignAndVerify_ShouldRoundTrip()
    {
        var key = DeriveKey("test-secret");
        var sign = Sign(key, "/path/to/file.txt");
        var isValid = Verify(key, sign, "/path/to/file.txt");
        await Assert.That(isValid).IsTrue();
    }

    [Test]
    public async Task Verify_WrongPath_ShouldReturnFalse()
    {
        var key = DeriveKey("test-secret");
        var sign = Sign(key, "/path/to/file.txt");
        var isValid = Verify(key, sign, "/path/to/other.txt");
        await Assert.That(isValid).IsFalse();
    }

    [Test]
    public async Task Verify_WrongKey_ShouldReturnFalse()
    {
        var key1 = DeriveKey("secret-1");
        var key2 = DeriveKey("secret-2");
        var sign = Sign(key1, "/path/to/file.txt");
        var isValid = Verify(key2, sign, "/path/to/file.txt");
        await Assert.That(isValid).IsFalse();
    }

    [Test]
    public async Task Sign_ShouldProduceNonEmptyString()
    {
        var key = DeriveKey("test-secret");
        var sign = Sign(key, "/test");
        await Assert.That(sign).IsNotNull().And.IsNotEmpty();
    }

    [Test]
    public async Task Verify_EmptySign_ShouldReturnFalse()
    {
        var key = DeriveKey("test-secret");
        var isValid = Verify(key, "", "/test");
        await Assert.That(isValid).IsFalse();
    }

    [Test]
    public async Task Verify_NullSign_ShouldReturnFalse()
    {
        var key = DeriveKey("test-secret");
        var isValid = Verify(key, null!, "/test");
        await Assert.That(isValid).IsFalse();
    }

    [Test]
    public async Task Verify_ExpiredSign_ShouldReturnFalse()
    {
        var key = DeriveKey("test-secret");
        // Create an already-expired sign (1 hour ago)
        var expire = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var sign = ComputeSign(key, "/test", expire);
        var isValid = Verify(key, sign, "/test");
        await Assert.That(isValid).IsFalse();
    }

    [Test]
    public async Task Verify_NeverExpires_ShouldWork()
    {
        var key = DeriveKey("test-secret");
        var sign = ComputeSign(key, "/test", 0); // 0 = never expires
        var isValid = Verify(key, sign, "/test");
        await Assert.That(isValid).IsTrue();
    }
}
