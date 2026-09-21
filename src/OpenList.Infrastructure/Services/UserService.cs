using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 用户增删改查 + 双因子缓存管理。密码使用 SHA256 加盐哈希（带固定后缀）。
/// 对应 Go 参考实现 <c>internal/op/user.go</c>。
/// </summary>
public sealed class UserService : IUserService
{
    private readonly OpenListDbContext _db;
    private readonly TimeProvider _time;
    private readonly AppConfiguration _config;

    /// <summary>
    /// 创建用户服务实例。
    /// </summary>
    /// <param name="db">EF Core 数据库上下文。</param>
    /// <param name="config">应用配置（读取超级管理员用户名）。</param>
    /// <param name="time">时间源（可选，默认系统时区）。</param>
    public UserService(OpenListDbContext db, AppConfiguration config, TimeProvider? time = null)
    {
        _db = db;
        _config = config;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// 配置文件中定义的超级管理员用户名。该用户每次启动时从配置同步，
    /// 不能通过 API 被降权、删除或改名。
    /// </summary>
    public string SuperAdminUsername => _config.AdminUsername;

    /// <summary>判断给定用户是否为配置中定义的超级管理员。</summary>
    public bool IsSuperAdmin(User u) =>
        u.Role == UserRole.Admin && string.Equals(u.Username, _config.AdminUsername, StringComparison.Ordinal);

    /// <summary>
    /// 按 Id 获取用户。
    /// </summary>
    public async Task<Resp<User>> GetAsync(long id, CancellationToken ct = default)
    {
        var u = await _db.Users.FindAsync([id], ct);
        return u is null ? Resp<User>.Fail("user not found", RespCodes.NotFound) : Resp<User>.Ok(u);
    }

    /// <summary>
    /// 按用户名获取用户。
    /// </summary>
    public async Task<Resp<User>> GetByNameAsync(string username, CancellationToken ct = default)
    {
        var u = await _db.Users.FirstOrDefaultAsync(x => x.Username == username, ct);
        return u is null ? Resp<User>.Fail("user not found", RespCodes.NotFound) : Resp<User>.Ok(u);
    }

    /// <summary>
    /// 获取系统中的首个管理员账号（按 Id 升序）。供初始化向导判断系统是否已初始化。
    /// 对应 Go <c>op.GetAdmin</c>：无管理员时返回 404（对应 gorm.ErrRecordNotFound）。
    /// </summary>
    public async Task<Resp<User>> GetAdminAsync(CancellationToken ct = default)
    {
        var u = await _db.Users.AsNoTracking()
            .Where(x => x.Role == UserRole.Admin)
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(ct);
        return u is null ? Resp<User>.Fail("admin not found", RespCodes.NotFound) : Resp<User>.Ok(u);
    }

    /// <summary>
    /// 按 SSO Id 获取用户。
    /// </summary>
    public async Task<Resp<User>> GetBySsoIdAsync(string ssoId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ssoId)) return Resp<User>.Fail("sso id required", RespCodes.BadRequest);
        var u = await _db.Users.FirstOrDefaultAsync(x => x.SsoID == ssoId, ct);
        return u is null ? Resp<User>.Fail("user not found", RespCodes.NotFound) : Resp<User>.Ok(u);
    }

    /// <summary>
    /// 获取当前用户对外可见的信息（脱敏 DTO）。
    /// </summary>
    public async Task<Resp<UserPublic>> GetCurrentAsync(long userId, CancellationToken ct = default)
    {
        var u = await _db.Users.FindAsync([userId], ct);
        return u is null ? Resp<UserPublic>.Fail("user not found", RespCodes.NotFound) : Resp<UserPublic>.Ok(ToPublic(u));
    }

    /// <summary>
    /// 更新当前用户自己的信息（用户名、密码、禁用状态、SSO Id 等）。
    /// </summary>
    public async Task<Resp> UpdateCurrentAsync(long userId, UpdateUserReq req, CancellationToken ct = default)
    {
        var u = await _db.Users.FindAsync([userId], ct);
        if (u is null) return Resp.Fail("user not found", RespCodes.NotFound);

        if (!string.IsNullOrWhiteSpace(req.Username)) u.Username = req.Username;
        if (!string.IsNullOrWhiteSpace(req.Password)) u.PasswordHash = HashPassword(req.Password);
        if (req.Disabled.HasValue) u.Disabled = req.Disabled.Value;
        if (req.SsoID is not null) u.SsoID = string.IsNullOrWhiteSpace(req.SsoID) ? null : req.SsoID;
        u.UpdatedAt = _time.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    /// <summary>
    /// 分页列出所有用户的对外信息，按 Id 升序。
    /// </summary>
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

    /// <summary>
    /// 创建用户：校验用户名/密码非空、用户名唯一后入库。
    /// </summary>
    /// <param name="req">创建用户请求。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回新用户的对外信息；用户名重复返回 409。</returns>
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
            AllowLdap = req.AllowLdap,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Users.Add(u);
        await _db.SaveChangesAsync(ct);
        return Resp<UserPublic>.Ok(ToPublic(u));
    }

    /// <summary>
    /// 更新指定用户的信息；对配置中定义的超级管理员限制降权、改名与禁用。
    /// </summary>
    public async Task<Resp> UpdateAsync(long id, UpdateUserReq req, CancellationToken ct = default)
    {
        var u = await _db.Users.FindAsync([id], ct);
        if (u is null) return Resp.Fail("user not found", RespCodes.NotFound);

        // 配置定义的超级管理员不能被降权、改名或禁用——其凭据归属配置管理。
        if (IsSuperAdmin(u))
        {
            if (req.Role.HasValue && req.Role.Value != UserRole.Admin)
                return Resp.Fail("cannot demote super admin", RespCodes.Forbidden);
            if (!string.IsNullOrWhiteSpace(req.Username) &&
                !string.Equals(req.Username, _config.AdminUsername, StringComparison.Ordinal))
                return Resp.Fail("cannot rename super admin", RespCodes.Forbidden);
            if (req.Disabled.HasValue && req.Disabled.Value)
                return Resp.Fail("cannot disable super admin", RespCodes.Forbidden);
        }

        if (!string.IsNullOrWhiteSpace(req.Username)) u.Username = req.Username;
        if (!string.IsNullOrWhiteSpace(req.Password)) u.PasswordHash = HashPassword(req.Password);
        if (req.Role.HasValue) u.Role = req.Role.Value;
        if (req.Permission.HasValue) u.Permission = req.Permission.Value;
        if (req.BasePath is not null) u.BasePath = req.BasePath;
        if (req.Disabled.HasValue) u.Disabled = req.Disabled.Value;
        if (req.TwoFactorEnabled.HasValue) u.TwoFactorEnabled = req.TwoFactorEnabled.Value;
        if (req.AllowLdap.HasValue) u.AllowLdap = req.AllowLdap.Value;
        if (req.SsoID is not null) u.SsoID = string.IsNullOrWhiteSpace(req.SsoID) ? null : req.SsoID;
        u.UpdatedAt = _time.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    /// <summary>
    /// 删除用户（超级管理员与管理 API 调用外的管理员用户不可删除）。
    /// </summary>
    public async Task<Resp> DeleteAsync(long id, CancellationToken ct = default)
    {
        var u = await _db.Users.FindAsync([id], ct);
        if (u is null) return Resp.Fail("user not found", RespCodes.NotFound);
        if (IsSuperAdmin(u))
            return Resp.Fail("cannot delete super admin", RespCodes.Forbidden);
        if (u.Role == UserRole.Admin)
            return Resp.Fail("cannot delete admin user", RespCodes.BadRequest);
        _db.Users.Remove(u);
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    /// <summary>
    /// 关闭指定用户的双因子并清空其双因子密钥。
    /// </summary>
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

    /// <summary>
    /// 持久化指定用户的双因子密钥。
    /// </summary>
    public async Task<Resp> SetTwoFactorSecretAsync(long id, string secret, CancellationToken ct = default)
    {
        var u = await _db.Users.FindAsync([id], ct);
        if (u is null) return Resp.Fail("user not found", RespCodes.NotFound);
        u.TwoFactorSecret = secret;
        u.UpdatedAt = _time.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    /// <summary>
    /// 清理指定用户缓存。当前尚无缓存层（Phase 16），为未来缓存失效预留的占位实现。
    /// </summary>
    public Task<Resp> DelCacheAsync(long id, CancellationToken ct = default)
    {
        // 尚未实现缓存层（Phase 16），此处为用户缓存失效预留占位。
        return Task.FromResult(Resp.Ok());
    }

    /// <summary>
    /// 列出指定用户的 SSH 公钥，按 Id 倒序。
    /// </summary>
    public async Task<Resp<List<SshKey>>> ListSshKeysAsync(long userId, CancellationToken ct = default)
    {
        var list = await _db.SshKeys.AsNoTracking()
            .Where(k => k.UserId == userId)
            .OrderByDescending(k => k.Id)
            .ToListAsync(ct);
        return Resp<List<SshKey>>.Ok(list);
    }

    /// <summary>
    /// 删除指定 SSH 公钥。
    /// </summary>
    public async Task<Resp> DeleteSshKeyAsync(long keyId, CancellationToken ct = default)
    {
        var k = await _db.SshKeys.FindAsync([keyId], ct);
        if (k is null) return Resp.Fail("ssh key not found", RespCodes.NotFound);
        _db.SshKeys.Remove(k);
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    /// <summary>
    /// 计算 <c>password + "-https://github.com/alist-org/alist"</c> 的 SHA256 哈希，返回小写十六进制串。
    /// 与 Go 参考实现一致，使前端客户端哈希可直接由 <see cref="LoginHashAsync"/> 校验。
    /// </summary>
    public static string HashPassword(string password)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(password + PasswordHashSuffix);
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 常量时间校验给定明文密码是否与存储的哈希匹配。
    /// </summary>
    public static bool VerifyPassword(string password, string storedHash)
    {
        if (string.IsNullOrEmpty(storedHash)) return false;
        var actual = HashPassword(password);
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(actual),
            System.Text.Encoding.UTF8.GetBytes(storedHash));
    }

    /// <summary>哈希密码前追加的后缀，与 Go 参考实现保持一致。</summary>
    public const string PasswordHashSuffix = "-https://github.com/alist-org/alist";

    /// <summary>
    /// 将用户实体映射为对外可见的信息 DTO。
    /// </summary>
    private static UserPublic ToPublic(User u) => new()
    {
        Id = u.Id,
        Username = u.Username,
        Role = u.Role,
        Disabled = u.Disabled,
        Permission = u.Permission,
        BasePath = u.BasePath,
        TwoFactorEnabled = u.TwoFactorEnabled,
        SsoID = u.SsoID,
        AllowLdap = u.AllowLdap,
        CreatedAt = u.CreatedAt,
        UpdatedAt = u.UpdatedAt,
    };
}
