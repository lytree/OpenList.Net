using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using OtpNet;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// JWT issuance/validation + TOTP 2FA. Tokens are HS256, no <c>"Bearer "</c> prefix on the wire.
/// Logout uses an in-memory revoked-token set (Phase 16 will swap for distributed cache).
/// Mirrors OpenList Go <c>internal/op/auth.go</c>.
/// </summary>
public sealed class JwtService : IAuthService
{
    private readonly IUserService _users;
    private readonly AppConfiguration _config;
    private readonly TimeProvider _time;
    private readonly ILdapAuthenticator? _ldap;
    private readonly HashSet<string> _revoked = new(StringComparer.Ordinal);

    public JwtService(IUserService users, AppConfiguration config, TimeProvider? time = null, ILdapAuthenticator? ldap = null)
    {
        _users = users;
        _config = config;
        _time = time ?? TimeProvider.System;
        _ldap = ldap;
    }

    public async Task<Resp<string>> LoginAsync(string username, string password, string? otpCode = null, CancellationToken ct = default)
    {
        var r = await _users.GetByNameAsync(username, ct);
        if (!r.IsSuccess()) return Resp<string>.Fail("wrong username or password", RespCodes.Unauthorized);
        var u = r.Data!;
        if (u.Disabled) return Resp<string>.Fail("user disabled", RespCodes.Forbidden);
        if (!UserService.VerifyPassword(password, u.PasswordHash))
            return Resp<string>.Fail("wrong username or password", RespCodes.Unauthorized);

        if (u.TwoFactorEnabled)
        {
            if (string.IsNullOrEmpty(otpCode)) return Resp<string>.Fail("2fa required", 402);
            if (!VerifyOtp(u.TwoFactorSecret!, otpCode))
                return Resp<string>.Fail("invalid 2fa code", RespCodes.Unauthorized);
        }
        return Resp<string>.Ok(IssueToken(u));
    }

    public Task<Resp<string>> LoginHashAsync(string username, string passwordHash, string? otpCode = null, CancellationToken ct = default)
    {
        // Hash-based login is used by the frontend when it already has the hashed password
        // (e.g. SSO bridge). For now we accept it directly.
        return LoginWithHashInternalAsync(username, passwordHash, otpCode, ct);
    }

    private async Task<Resp<string>> LoginWithHashInternalAsync(string username, string passwordHash, string? otpCode, CancellationToken ct)
    {
        var r = await _users.GetByNameAsync(username, ct);
        if (!r.IsSuccess()) return Resp<string>.Fail("wrong username or password", RespCodes.Unauthorized);
        var u = r.Data!;
        if (u.Disabled) return Resp<string>.Fail("user disabled", RespCodes.Forbidden);
        if (!VerifyHash(u.PasswordHash, passwordHash))
            return Resp<string>.Fail("wrong username or password", RespCodes.Unauthorized);

        if (u.TwoFactorEnabled)
        {
            if (string.IsNullOrEmpty(otpCode)) return Resp<string>.Fail("2fa required", 402);
            if (!VerifyOtp(u.TwoFactorSecret!, otpCode))
                return Resp<string>.Fail("invalid 2fa code", RespCodes.Unauthorized);
        }
        return Resp<string>.Ok(IssueToken(u));
    }

    /// <summary>
    /// Constant-time comparison of two hex password hashes. Returns false on any
    /// length mismatch instead of throwing.
    /// </summary>
    private static bool VerifyHash(string storedHash, string providedHash)
    {
        if (string.IsNullOrEmpty(storedHash) || string.IsNullOrEmpty(providedHash)) return false;
        if (storedHash.Length != providedHash.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(storedHash),
            System.Text.Encoding.UTF8.GetBytes(providedHash));
    }

    public async Task<Resp<string>> LoginLdapAsync(string username, string password, CancellationToken ct = default)
    {
        // LDAP login flow:
        //   1. Authenticator verifies (username, password) against the directory.
        //   2. Look up the local user by name.
        //      - If they exist and have AllowLdap enabled, issue a token.
        //      - If they don't exist, auto-provision a disabled-password local account
        //        (random password hash, General role) so LDAP users can log in without
        //        admin pre-registration — matching OpenList Go behaviour.
        //   3. Existing users without AllowLdap are rejected so admins keep control.
        if (_ldap is null || !_ldap.Enabled)
            return Resp<string>.Fail("ldap not configured", RespCodes.NotImplemented);

        var ok = await _ldap.AuthenticateAsync(username, password, ct);
        if (!ok) return Resp<string>.Fail("wrong username or password", RespCodes.Unauthorized);

        var r = await _users.GetByNameAsync(username, ct);
        User u;
        if (r.IsSuccess())
        {
            u = r.Data!;
            if (u.Disabled) return Resp<string>.Fail("user disabled", RespCodes.Forbidden);
            if (!u.AllowLdap)
                return Resp<string>.Fail("ldap login not allowed for this user", RespCodes.Forbidden);
        }
        else
        {
            // Auto-provision: a random 32-byte hash makes the password route unusable,
            // so the local account is effectively LDAP-only.
            var create = await _users.CreateAsync(new CreateUserReq
            {
                Username = username,
                Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                Role = UserRole.General,
                AllowLdap = true,
            }, ct);
            if (!create.IsSuccess()) return Resp<string>.Fail(create.Message, create.Code);
            var fetched = await _users.GetByNameAsync(username, ct);
            if (!fetched.IsSuccess()) return Resp<string>.Fail("ldap user provisioning failed", RespCodes.InternalError);
            u = fetched.Data!;
        }
        return Resp<string>.Ok(IssueToken(u));
    }

    public async Task<Resp<string>> Generate2FaAsync(long userId, CancellationToken ct = default)
    {
        var r = await _users.GetAsync(userId, ct);
        if (!r.IsSuccess()) return Resp<string>.Fail("user not found", RespCodes.NotFound);
        var secret = GenerateOtpSecret();
        // Persist the secret; 2FA stays disabled until Verify2FaAsync confirms a valid code.
        await _users.SetTwoFactorSecretAsync(userId, secret, ct);
        return Resp<string>.Ok(secret);
    }

    public async Task<Resp> Verify2FaAsync(long userId, string code, CancellationToken ct = default)
    {
        var r = await _users.GetAsync(userId, ct);
        if (!r.IsSuccess()) return Resp.Fail("user not found", RespCodes.NotFound);
        var u = r.Data!;
        if (string.IsNullOrEmpty(u.TwoFactorSecret))
            return Resp.Fail("2fa not initialized", RespCodes.BadRequest);
        if (!VerifyOtp(u.TwoFactorSecret, code))
            return Resp.Fail("invalid 2fa code", RespCodes.Unauthorized);

        await _users.UpdateAsync(userId, new UpdateUserReq { TwoFactorEnabled = true }, ct);
        return Resp.Ok();
    }

    public Task<Resp> LogoutAsync(string token, CancellationToken ct = default)
    {
        _revoked.Add(token);
        return Task.FromResult(Resp.Ok());
    }

    public Task<long?> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        if (_revoked.Contains(token)) return Task.FromResult<long?>(null);
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var parameters = BuildValidationParameters();
            var principal = handler.ValidateToken(token, parameters, out _);
            var idClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return Task.FromResult<long?>(long.TryParse(idClaim, out var id) ? id : null);
        }
        catch
        {
            return Task.FromResult<long?>(null);
        }
    }

    public string IssueToken(User user)
    {
        var now = _time.GetUtcNow();
        var expires = now.AddHours(_config.TokenExpiresIn);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, ((int)user.Role).ToString()),
        };
        var creds = new SigningCredentials(BuildSymmetricKey(_config.JwtSecret), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public TokenValidationParameters BuildValidationParameters()
        => BuildValidationParameters(_config.JwtSecret);

    /// <summary>Static helper for use at DI configuration time (no service resolution needed).</summary>
    public static TokenValidationParameters BuildValidationParameters(string jwtSecret)
        => new()
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = BuildSymmetricKey(jwtSecret),
            ClockSkew = TimeSpan.FromSeconds(30),
        };

    private static SymmetricSecurityKey BuildSymmetricKey(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            // Auto-generate 32 hex chars (2×Guid) — matches AGENTS.md quirk.
            secret = (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"))[..32];
        }
        return new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(secret));
    }

    private static string GenerateOtpSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        return Base32Encoding.ToString(bytes);
    }

    private static bool VerifyOtp(string secret, string code)
    {
        try
        {
            var totp = new Totp(Base32Encoding.ToBytes(secret));
            return totp.VerifyTotp(code, out _, new VerificationWindow(1, 1));
        }
        catch { return false; }
    }
}

internal static class RespExtensions
{
    public static bool IsSuccess<T>(this Resp<T> r) => r.Code == RespCodes.Ok;
}
