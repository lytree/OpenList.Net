using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Services;

namespace OpenList.Infrastructure.Auth;

public class AuthService : IAuthService
{
    private readonly OpenListDbContext _db;
    private readonly string _jwtSecret;
    private readonly int _tokenExpiresIn;
    private readonly ConcurrentDictionary<string, DateTime> _blacklistedTokens = new();
    private static readonly ConcurrentDictionary<string, int> _loginAttempts = new();
    private static readonly TimeSpan _lockDuration = TimeSpan.FromMinutes(5);
    private const int MaxAuthRetries = 5;

    public AuthService(OpenListDbContext db, IConfiguration config)
    {
        _db = db;
        _jwtSecret = config["JwtSecret"] ?? Guid.NewGuid().ToString("N")[..16];
        _tokenExpiresIn = config.GetValue("TokenExpiresIn", 48);
    }

    public async Task<string?> LoginAsync(string username, string password, string? otpCode = null, CancellationToken ct = default)
    {
        var passwordHash = UserService.StaticHash(password);
        return await LoginHashAsync(username, passwordHash, otpCode, ct);
    }

    public async Task<string?> LoginHashAsync(string username, string passwordHash, string? otpCode = null, CancellationToken ct = default)
    {
        // Check login attempts
        if (_loginAttempts.TryGetValue(username, out var attempts) && attempts >= MaxAuthRetries)
            return null; // Too many attempts

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
        if (user == null || user.Disabled)
        {
            _loginAttempts.AddOrUpdate(username, 1, (_, v) => v + 1);
            return null;
        }

        if (!UserService.ValidatePwdStaticHash(user, passwordHash))
        {
            _loginAttempts.AddOrUpdate(username, 1, (_, v) => v + 1);
            return null;
        }

        // Check 2FA
        if (!string.IsNullOrEmpty(user.OtpSecret))
        {
            if (string.IsNullOrEmpty(otpCode)) return null;
            // TOTP validation would go here
        }

        _loginAttempts.TryRemove(username, out _);
        return GenerateToken(user);
    }

    public async Task<string?> LoginLdapAsync(string username, string password, CancellationToken ct = default)
    {
        // LDAP authentication would be implemented here
        return await Task.FromResult<string?>(null);
    }

    public async Task<User?> GetCurrentUserAsync(string token, CancellationToken ct = default)
    {
        var principal = ValidateToken(token);
        if (principal == null) return null;

        var userIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim == null || !uint.TryParse(userIdClaim.Value, out var userId)) return null;

        return await _db.Users.FindAsync([userId], ct);
    }

    public async Task<(string qr, string secret)> Generate2FAAsync(uint userId, CancellationToken ct = default)
    {
        var secret = GenerateSecret();
        // QR code generation would use QRCoder library
        var qr = $"otpauth://totp/OpenList:user?secret={secret}&issuer=OpenList";
        return await Task.FromResult((qr, secret));
    }

    public async Task<bool> Verify2FAAsync(uint userId, string code, string secret, CancellationToken ct = default)
    {
        // TOTP validation would go here
        var user = await _db.Users.FindAsync([userId], ct);
        if (user == null) return false;
        user.OtpSecret = secret;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public Task InvalidateTokenAsync(string token, CancellationToken ct = default)
    {
        _blacklistedTokens[token.Replace("Bearer ", "")] = DateTime.UtcNow;
        return Task.CompletedTask;
    }

    public string HashPassword(string password) => UserService.StaticHash(password);

    private string GenerateToken(User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
        };

        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.AddHours(_tokenExpiresIn),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private ClaimsPrincipal? ValidateToken(string token)
    {
        token = token.Replace("Bearer ", "");
        if (_blacklistedTokens.ContainsKey(token)) return null;

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret));
        var handler = new JwtSecurityTokenHandler();

        try
        {
            return handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidateIssuer = false,
                ValidateAudience = false,
                ClockSkew = TimeSpan.Zero
            }, out _);
        }
        catch
        {
            return null;
        }
    }

    private static string GenerateSecret()
    {
        var bytes = new byte[20];
        RandomNumberGenerator.Fill(bytes);
        return Base32Encode(bytes);
    }

    private static string Base32Encode(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var result = new StringBuilder();
        for (int i = 0; i < data.Length; i += 5)
        {
            ulong bits = 0;
            int nBytes = Math.Min(5, data.Length - i);
            for (int j = 0; j < nBytes; j++)
                bits = (bits << 8) | data[i + j];
            int nBits = nBytes * 8;
            for (int j = 4; j >= 0; j--)
            {
                if (j * 5 + 5 <= nBits)
                    result.Append(alphabet[(int)((bits >> (j * 5)) & 0x1F)]);
            }
        }
        return result.ToString();
    }
}
