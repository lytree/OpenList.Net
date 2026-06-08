using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

public class UserService : IUserService
{
    private readonly OpenListDbContext _db;
    private const string StaticHashSalt = "https://github.com/alist-org/alist";

    public UserService(OpenListDbContext db) => _db = db;

    public async Task<User?> GetUserByNameAsync(string username, CancellationToken ct = default)
        => await _db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);

    public async Task<User?> GetUserByIdAsync(uint id, CancellationToken ct = default)
        => await _db.Users.FindAsync([id], ct);

    public async Task<(List<User> Users, int Total)> ListUsersAsync(int page, int perPage, CancellationToken ct = default)
    {
        var total = await _db.Users.CountAsync(ct);
        var users = await _db.Users.Skip((page - 1) * perPage).Take(perPage).ToListAsync(ct);
        return (users, total);
    }

    public async Task<User> CreateUserAsync(User user, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(user.Salt))
            user.Salt = GenerateSalt();
        if (!string.IsNullOrEmpty(user.BasePath) && user.BasePath != "/")
            user.PwdHash = HashPwd(StaticHash(user.BasePath), user.Salt);
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        return user;
    }

    public async Task UpdateUserAsync(User user, CancellationToken ct = default)
    {
        _db.Users.Update(user);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteUserAsync(uint id, CancellationToken ct = default)
    {
        var user = await _db.Users.FindAsync([id], ct);
        if (user != null)
        {
            _db.Users.Remove(user);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task SetPasswordAsync(User user, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(user.Salt))
            user.Salt = GenerateSalt();
        user.PwdHash = HashPwd(StaticHash(password), user.Salt);
        _db.Users.Update(user);
        await _db.SaveChangesAsync(ct);
    }

    public async Task Cancel2FAAsync(uint id, CancellationToken ct = default)
    {
        var user = await _db.Users.FindAsync([id], ct);
        if (user != null)
        {
            user.OtpSecret = null;
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task DeleteUserCacheAsync(uint id, CancellationToken ct = default)
    {
        // Cache clearing would be implemented with the cache service
        await Task.CompletedTask;
    }

    public async Task DeleteUserCacheByUsernameAsync(string username, CancellationToken ct = default)
    {
        // Cache clearing would be implemented with the cache service
        await Task.CompletedTask;
    }

    public async Task<User?> GetUserBySsoIdAsync(string ssoId, CancellationToken ct = default)
    {
        return await _db.Users.FirstOrDefaultAsync(u => u.SsoID == ssoId, ct);
    }

    public async Task<User?> GetGuestUserAsync(CancellationToken ct = default)
    {
        return await _db.Users.FirstOrDefaultAsync(u => u.Role == UserRole.Guest, ct);
    }

    public async Task<List<SshKey>> ListSshKeysAsync(uint userId, CancellationToken ct = default)
    {
        return await _db.SshKeys.Where(k => k.UserId == userId).ToListAsync(ct);
    }

    public async Task<(List<SshKey> Keys, int Total)> ListSshKeysPagedAsync(uint userId, int page, int perPage, CancellationToken ct = default)
    {
        var query = _db.SshKeys.Where(k => k.UserId == userId);
        var total = await query.CountAsync(ct);
        var keys = await query.Skip((page - 1) * perPage).Take(perPage).ToListAsync(ct);
        return (keys, total);
    }

    public async Task AddSshKeyAsync(uint userId, string name, string key, CancellationToken ct = default)
    {
        _db.SshKeys.Add(new SshKey { Name = name, PublicKey = key, UserId = userId });
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteSshKeyAsync(uint userId, string name, CancellationToken ct = default)
    {
        var key = await _db.SshKeys.FirstOrDefaultAsync(k => k.UserId == userId && k.Name == name, ct);
        if (key != null)
        {
            _db.SshKeys.Remove(key);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task DeleteSshKeyByIdAsync(uint id, CancellationToken ct = default)
    {
        var key = await _db.SshKeys.FindAsync([id], ct);
        if (key != null)
        {
            _db.SshKeys.Remove(key);
            await _db.SaveChangesAsync(ct);
        }
    }

    public static string StaticHash(string password)
        => HashData(SHA256.HashData(Encoding.UTF8.GetBytes($"{password}-{StaticHashSalt}")));

    public static string HashPwd(string staticHash, string salt)
        => HashData(SHA256.HashData(Encoding.UTF8.GetBytes($"{staticHash}-{salt}")));

    public static string TwoHashPwd(string password, string salt)
        => HashPwd(StaticHash(password), salt);

    public static bool ValidatePwdStaticHash(User user, string pwdStaticHash)
        => user.PwdHash == HashPwd(pwdStaticHash, user.Salt);

    private static string GenerateSalt()
    {
        var bytes = new byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string HashData(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();
}
