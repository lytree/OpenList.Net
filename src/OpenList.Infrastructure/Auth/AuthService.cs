using System.Collections.Concurrent;
using System.Drawing;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Novell.Directory.Ldap;
using OtpNet;
using QRCoder;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Services;

namespace OpenList.Infrastructure.Auth;

public class AuthService : IAuthService
{
    private readonly OpenListDbContext _db;
    private readonly IConfiguration _config;
    private readonly string _jwtSecret;
    private readonly int _tokenExpiresIn;
    private readonly ConcurrentDictionary<string, DateTime> _blacklistedTokens = new();
    private static readonly ConcurrentDictionary<string, int> _loginAttempts = new();
    private static readonly TimeSpan _lockDuration = TimeSpan.FromMinutes(5);
    private const int MaxAuthRetries = 5;

    public AuthService(OpenListDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
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

        // Check 2FA - return special marker to indicate OTP required
        if (!string.IsNullOrEmpty(user.OtpSecret))
        {
            if (string.IsNullOrEmpty(otpCode) || !ValidateTotpCode(user.OtpSecret, otpCode))
                return "2FA_REQUIRED";
        }

        _loginAttempts.TryRemove(username, out _);
        return GenerateToken(user);
    }

    public async Task<string?> LoginLdapAsync(string username, string password, CancellationToken ct = default)
    {
        var ldapEnabled = _config["LdapLoginEnabled"] == "true";
        if (!ldapEnabled) return null;

        var ldapServer = _config["LdapServer"] ?? "";
        var skipTlsVerify = _config["LdapSkipTlsVerify"] == "true";
        var managerDN = _config["LdapManagerDN"] ?? "";
        var managerPassword = _config["LdapManagerPassword"] ?? "";
        var searchBase = _config["LdapUserSearchBase"] ?? "";
        var searchFilter = _config["LdapUserSearchFilter"] ?? "(uid=%s)";

        if (string.IsNullOrEmpty(ldapServer) || string.IsNullOrEmpty(searchBase))
            return null;

        // LDAP operations are synchronous in Novell.Directory.Ldap — run on thread pool
        var ldapOk = await Task.Run(() =>
        {
            try
            {
                var (host, port, useSsl) = ParseLdapServer(ldapServer);

                using var connection = new LdapConnection();
                connection.SecureSocketLayer = useSsl;
                connection.Connect(host, port);
                if (useSsl && skipTlsVerify)
                    connection.StartTls();

                // Bind with manager DN if configured
                if (!string.IsNullOrEmpty(managerDN) && !string.IsNullOrEmpty(managerPassword))
                    connection.Bind(managerDN, managerPassword);

                // Search for the user
                var escapedUsername = username.Replace("\\", "\\5c").Replace("*", "\\2a")
                    .Replace("(", "\\28").Replace(")", "\\29").Replace("/", "\\2f");
                var filter = searchFilter.Replace("%s", escapedUsername);
                var results = connection.Search(searchBase, LdapConnection.ScopeSub, filter, ["dn"], false);

                string? userDN = null;
                while (results.HasMore())
                {
                    var entry = results.Next();
                    if (userDN != null) return false; // Multiple entries
                    userDN = entry.Dn;
                }

                if (string.IsNullOrEmpty(userDN)) return false;

                // Bind as the user to verify password
                connection.Bind(userDN, password);
                return connection.Bound;
            }
            catch
            {
                return false;
            }
        }, ct);

        if (!ldapOk) return null;

        // Find or auto-register user
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username && u.AllowLdap, ct);
        if (user == null)
        {
            var autoRegister = _config["LdapAutoRegister"] == "true";
            if (!autoRegister) return null;

            user = new User
            {
                Username = username,
                PwdHash = UserService.StaticHash(Guid.NewGuid().ToString("N")),
                Permission = int.TryParse(_config["LdapDefaultPermission"], out var perm) ? perm : 0,
                BasePath = _config["LdapDefaultDir"] ?? "/",
                AllowLdap = true
            };
            _db.Users.Add(user);
            await _db.SaveChangesAsync(ct);
        }

        return GenerateToken(user);
    }

    private static (string host, int port, bool useSsl) ParseLdapServer(string server)
    {
        var useSsl = false;
        if (server.StartsWith("ldaps://", StringComparison.OrdinalIgnoreCase))
        {
            useSsl = true;
            server = server["ldaps://".Length..];
        }
        else if (server.StartsWith("ldap://", StringComparison.OrdinalIgnoreCase))
        {
            server = server["ldap://".Length..];
        }

        var parts = server.Split(':');
        var host = parts[0];
        var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : (useSsl ? 636 : 389);
        return (host, port, useSsl);
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
        var user = await _db.Users.FindAsync([userId], ct);
        var username = user?.Username ?? "user";

        // Generate a new TOTP secret key
        var key = KeyGeneration.GenerateRandomKey(20);
        var secret = Base32Encoding.ToString(key);

        // Build the otpauth URI
        var issuer = Uri.EscapeDataString("OpenList");
        var account = Uri.EscapeDataString(username);
        var otpauthUri = $"otpauth://totp/{issuer}:{account}?secret={secret}&issuer={issuer}&algorithm=SHA1&digits=6&period=30";

        // Generate QR code as base64 PNG
        string qr;
        using (var qrGenerator = new QRCodeGenerator())
        using (var qrCodeData = qrGenerator.CreateQrCode(otpauthUri, QRCodeGenerator.ECCLevel.M))
        using (var qrCode = new PngByteQRCode(qrCodeData))
        {
            var pngBytes = qrCode.GetGraphic(10);
            qr = "data:image/png;base64," + Convert.ToBase64String(pngBytes);
        }

        return (qr, secret);
    }

    public async Task<bool> Verify2FAAsync(uint userId, string code, string secret, CancellationToken ct = default)
    {
        if (!ValidateTotpCode(secret, code))
            return false;

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

    /// <summary>
    /// Removes expired entries from the token blacklist and login attempt tracker.
    /// Should be called periodically (e.g., via a background service).
    /// </summary>
    public void CleanupExpiredEntries()
    {
        // Remove blacklisted tokens that have expired (older than token expiration window)
        var cutoff = DateTime.UtcNow.AddHours(-_tokenExpiresIn);
        foreach (var kvp in _blacklistedTokens)
        {
            if (kvp.Value < cutoff)
                _blacklistedTokens.TryRemove(kvp.Key, out _);
        }

        // Remove login attempt entries that are past the lock duration
        // (they're already ineffective, just taking memory)
        // Since we don't track timestamps for login attempts, just clear all
        // if any entry exceeds MaxAuthRetries and enough time has passed
        if (_loginAttempts.Count > 100)
            _loginAttempts.Clear();
    }

    public string HashPassword(string password) => UserService.StaticHash(password);

    public string GenerateToken(User user)
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

        // Lazy cleanup: remove expired blacklist entries periodically
        if (_blacklistedTokens.Count > 0 && DateTime.UtcNow.Minute == 0) // roughly once per hour
        {
            CleanupExpiredEntries();
        }

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

    /// <summary>
    /// Validates a TOTP code against the given secret key.
    /// </summary>
    private static bool ValidateTotpCode(string secret, string code)
    {
        try
        {
            var key = Base32Encoding.ToBytes(secret);
            var totp = new Totp(key, step: 30, totpSize: 6, mode: OtpHashMode.Sha1);
            return totp.VerifyTotp(code, out _, new VerificationWindow(1, 1));
        }
        catch
        {
            return false;
        }
    }
}
