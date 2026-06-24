using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// User CRUD + 2FA cache management. Passwords hashed with PBKDF2-SHA256 (100k iterations).
/// Mirrors OpenList Go <c>internal/op/user.go</c>.
/// </summary>
public sealed class UserService : IUserService
{
    private readonly OpenListDbContext _db;
    private readonly TimeProvider _time;

    public UserService(OpenListDbContext db, TimeProvider? time = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
    }

    public async Task<Resp<User>> GetAsync(long id, CancellationToken ct = default)
    {
        var u = await _db.Users.FindAsync([id], ct);
        return u is null ? Resp<User>.Fail("user not found", RespCodes.NotFound) : Resp<User>.Ok(u);
    }

    public async Task<Resp<User>> GetByNameAsync(string username, CancellationToken ct = default)
    {
        var u = await _db.Users.FirstOrDefaultAsync(x => x.Username == username, ct);
        return u is null ? Resp<User>.Fail("user not found", RespCodes.NotFound) : Resp<User>.Ok(u);
    }

    public async Task<Resp<UserPublic>> GetCurrentAsync(long userId, CancellationToken ct = default)
    {
        var u = await _db.Users.FindAsync([userId], ct);
        return u is null ? Resp<UserPublic>.Fail("user not found", RespCodes.NotFound) : Resp<UserPublic>.Ok(ToPublic(u));
    }

    public async Task<Resp> UpdateCurrentAsync(long userId, UpdateUserReq req, CancellationToken ct = default)
    {
        var u = await _db.Users.FindAsync([userId], ct);
        if (u is null) return Resp.Fail("user not found", RespCodes.NotFound);

        if (!string.IsNullOrWhiteSpace(req.Username)) u.Username = req.Username;
        if (!string.IsNullOrWhiteSpace(req.Password)) u.PasswordHash = HashPassword(req.Password);
        if (req.Disabled.HasValue) u.Disabled = req.Disabled.Value;
        u.UpdatedAt = _time.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    public async Task<Resp<List<UserPublic>>> ListAsync(CancellationToken ct = default)
    {
        var list = await _db.Users.AsNoTracking()
            .OrderBy(u => u.Id)
            .Select(u => new UserPublic
            {
                Id = u.Id,
                Username = u.Username,
                Role = u.Role,
                Disabled = u.Disabled,
                Permission = u.Permission,
                BasePath = u.BasePath,
                TwoFactorEnabled = u.TwoFactorEnabled,
                CreatedAt = u.CreatedAt,
                UpdatedAt = u.UpdatedAt,
            })
            .ToListAsync(ct);
        return Resp<List<UserPublic>>.Ok(list);
    }

    public async Task<Resp<UserPublic>> CreateAsync(CreateUserReq req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return Resp<UserPublic>.Fail("username and password required", RespCodes.BadRequest);

        if (await _db.Users.AnyAsync(u => u.Username == req.Username, ct))
            return Resp<UserPublic>.Fail("username already exists", RespCodes.Conflict);

        var now = _time.GetUtcNow();
        var u = new User
        {
            Username = req.Username,
            PasswordHash = HashPassword(req.Password),
            Role = req.Role,
            Permission = req.Permission,
            BasePath = req.BasePath,
            Disabled = req.Disabled,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Users.Add(u);
        await _db.SaveChangesAsync(ct);
        return Resp<UserPublic>.Ok(ToPublic(u));
    }

    public async Task<Resp> UpdateAsync(long id, UpdateUserReq req, CancellationToken ct = default)
    {
        var u = await _db.Users.FindAsync([id], ct);
        if (u is null) return Resp.Fail("user not found", RespCodes.NotFound);

        if (!string.IsNullOrWhiteSpace(req.Username)) u.Username = req.Username;
        if (!string.IsNullOrWhiteSpace(req.Password)) u.PasswordHash = HashPassword(req.Password);
        if (req.Role.HasValue) u.Role = req.Role.Value;
        if (req.Permission.HasValue) u.Permission = req.Permission.Value;
        if (req.BasePath is not null) u.BasePath = req.BasePath;
        if (req.Disabled.HasValue) u.Disabled = req.Disabled.Value;
        if (req.TwoFactorEnabled.HasValue) u.TwoFactorEnabled = req.TwoFactorEnabled.Value;
        u.UpdatedAt = _time.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    public async Task<Resp> DeleteAsync(long id, CancellationToken ct = default)
    {
        var u = await _db.Users.FindAsync([id], ct);
        if (u is null) return Resp.Fail("user not found", RespCodes.NotFound);
        if (u.Role == UserRole.Admin)
            return Resp.Fail("cannot delete admin user", RespCodes.BadRequest);
        _db.Users.Remove(u);
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    public async Task<Resp> Cancel2FaAsync(long id, CancellationToken ct = default)
    {
        var u = await _db.Users.FindAsync([id], ct);
        if (u is null) return Resp.Fail("user not found", RespCodes.NotFound);
        u.TwoFactorEnabled = false;
        u.TwoFactorSecret = null;
        u.UpdatedAt = _time.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    public Task<Resp> DelCacheAsync(long id, CancellationToken ct = default)
    {
        // No cache layer yet (Phase 16); placeholder for future user cache invalidation.
        return Task.FromResult(Resp.Ok());
    }

    public async Task<Resp<List<SshKey>>> ListSshKeysAsync(long userId, CancellationToken ct = default)
    {
        var list = await _db.SshKeys.AsNoTracking()
            .Where(k => k.UserId == userId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);
        return Resp<List<SshKey>>.Ok(list);
    }

    public async Task<Resp> DeleteSshKeyAsync(long keyId, CancellationToken ct = default)
    {
        var k = await _db.SshKeys.FindAsync([keyId], ct);
        if (k is null) return Resp.Fail("ssh key not found", RespCodes.NotFound);
        _db.SshKeys.Remove(k);
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    /// <summary>PBKDF2-SHA256, 100k iterations, base64. Salt is prepended (16 bytes).</summary>
    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(salt.Concat(hash).ToArray());
    }

    public static bool VerifyPassword(string password, string storedHash)
    {
        if (string.IsNullOrEmpty(storedHash)) return false;
        try
        {
            var bytes = Convert.FromBase64String(storedHash);
            if (bytes.Length != 48) return false; // 16 salt + 32 hash
            var salt = bytes[..16];
            var expected = bytes[16..];
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch { return false; }
    }

    private static UserPublic ToPublic(User u) => new()
    {
        Id = u.Id,
        Username = u.Username,
        Role = u.Role,
        Disabled = u.Disabled,
        Permission = u.Permission,
        BasePath = u.BasePath,
        TwoFactorEnabled = u.TwoFactorEnabled,
        CreatedAt = u.CreatedAt,
        UpdatedAt = u.UpdatedAt,
    };
}
