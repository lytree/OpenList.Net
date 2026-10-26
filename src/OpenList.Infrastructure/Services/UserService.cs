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
    private readonly UserTokenCutoffRegistry? _cutoffs;

    /// <summary>
    /// 创建用户服务实例。
    /// </summary>
    /// <param name="db">EF Core 数据库上下文。</param>
    /// <param name="config">应用配置（读取超级管理员用户名）。</param>
    /// <param name="time">时间源（可选，默认系统时区）。</param>
    /// <param name="cutoffs">token 失效登记表（可选；BasePath/权限位变更时用来即时失效既有 token）。</param>
    public UserService(OpenListDbContext db, AppConfiguration config, TimeProvider? time = null,
        UserTokenCutoffRegistry? cutoffs = null)
    {
        _db = db;
        _config = config;
        _time = time ?? TimeProvider.System;
        _cutoffs = cutoffs;
    }

    /// <summary>
    /// 内置管理员的用户名（仅用于展示与日志，不参与鉴权判定）。
    /// 数据库里真正的保护依据是 <see cref="User.IsBuiltIn"/> 标记，因此改过用户名后
    /// 保护依然生效。
    /// </summary>
    public string SuperAdminUsername => _config.AdminUsername;

    /// <summary>
    /// 判断给定用户是否为受保护的内置管理员（不可删除 / 改名 / 降权 / 禁用）。
    /// 依据数据库标记 <see cref="User.IsBuiltIn"/>，而非用户名比较 —— 用户通过管理界面
    /// 或桌面端改过用户名后，保护不应随之失效。
    /// </summary>
    public static bool IsSuperAdmin(User u) => u.IsProtectedAdmin();

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
        // 空串而非 null：旧库 SsoID 列是 INTEGER NOT NULL，写 null 会让保存抛
        // SQLite Error 19（见 User.SsoID 注释）。空串即"未绑定"。
        if (req.SsoID is not null) u.SsoID = string.IsNullOrWhiteSpace(req.SsoID) ? string.Empty : req.SsoID;
        u.UpdatedAt = _time.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    /// <summary>
    /// 列出所有用户的对外信息，按 Id 升序。
    /// </summary>
    /// <remarks>
    /// 投影必须与 <see cref="ToPublic"/> 保持同步：漏掉 <c>IsBuiltIn</c> 会让前端以为内置
    /// 管理员可以删除，从而放出注定被服务端拒绝的操作。
    /// </remarks>
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
                SsoID = u.SsoID,
                AllowLdap = u.AllowLdap,
                IsBuiltIn = u.IsBuiltIn,
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
            // 通过 API 创建的账号一律不是内置账号 —— "内置"是系统级保证，不该由调用方指定。
            IsBuiltIn = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Users.Add(u);
        await _db.SaveChangesAsync(ct);
        return Resp<UserPublic>.Ok(ToPublic(u));
    }

    /// <summary>
    /// 创建系统的初始管理员（Web 安装向导用）。带 <see cref="User.IsBuiltIn"/> 标记 ——
    /// 用户表非空说明系统已初始化，此时拒绝，避免向导被重复调用来追加管理员。
    /// </summary>
    public async Task<Resp<UserPublic>> CreateInitialAdminAsync(
        string username, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return Resp<UserPublic>.Fail("username and password required", RespCodes.BadRequest);

        // 判据是"已有管理员"而非"用户表非空"：初始化会自动创建 guest，
        // 用表判据会让安装向导在全新库上永远返回"已初始化"。
        if (await _db.Users.AnyAsync(u => u.Role == UserRole.Admin, ct))
            return Resp<UserPublic>.Fail("system has already been initialized", RespCodes.BadRequest);

        if (await _db.Users.AnyAsync(u => u.Username == username, ct))
            return Resp<UserPublic>.Fail("username already exists", RespCodes.Conflict);

        var now = _time.GetUtcNow();
        var u = new User
        {
            Username = username,
            PasswordHash = HashPassword(password),
            Role = UserRole.Admin,
            // 与 DbInitializer 的自动初始化路径保持同一位值，避免两种建号方式权限不同。
            Permission = 0x71FF,
            BasePath = "/",
            Disabled = false,
            IsBuiltIn = true,
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

        // 内置管理员不能被降权、改名或禁用 —— 但<em>可以</em>改密码，
        // 那是它被保留的唯一目的（改密入口见 ResetAdminPasswordAsync / 桌面端）。
        if (IsSuperAdmin(u))
        {
            if (req.Role.HasValue && req.Role.Value != UserRole.Admin)
                return Resp.Fail("cannot demote built-in admin", RespCodes.Forbidden);
            if (!string.IsNullOrWhiteSpace(req.Username) &&
                !string.Equals(req.Username, u.Username, StringComparison.Ordinal))
                return Resp.Fail("cannot rename built-in admin", RespCodes.Forbidden);
            if (req.Disabled.HasValue && req.Disabled.Value)
                return Resp.Fail("cannot disable built-in admin", RespCodes.Forbidden);
            if (req.Permission.HasValue)
                return Resp.Fail("cannot change built-in admin permissions", RespCodes.Forbidden);
        }

        // Role / Permission / BasePath 会被签进 token（见 JwtService.IssueToken），是签发时刻的
        // 权限快照。这三者一旦变化，既有 token 就不再反映最新权限 —— 必须让它们失效，
        // 否则旧 token 会带着旧权限继续有效到期。改动前的值先记下，改后再比对。
        var oldRole = u.Role;
        var oldPermission = u.Permission;
        var oldBasePath = u.BasePath;

        if (!string.IsNullOrWhiteSpace(req.Username)) u.Username = req.Username;
        if (!string.IsNullOrWhiteSpace(req.Password)) u.PasswordHash = HashPassword(req.Password);
        if (req.Role.HasValue) u.Role = req.Role.Value;
        if (req.Permission.HasValue) u.Permission = req.Permission.Value;
        if (req.BasePath is not null) u.BasePath = req.BasePath;
        if (req.Disabled.HasValue) u.Disabled = req.Disabled.Value;
        if (req.TwoFactorEnabled.HasValue) u.TwoFactorEnabled = req.TwoFactorEnabled.Value;
        if (req.AllowLdap.HasValue) u.AllowLdap = req.AllowLdap.Value;
        // 空串而非 null：旧库 SsoID 列是 INTEGER NOT NULL，写 null 会让保存抛
        // SQLite Error 19（见 User.SsoID 注释）。空串即"未绑定"。
        if (req.SsoID is not null) u.SsoID = string.IsNullOrWhiteSpace(req.SsoID) ? string.Empty : req.SsoID;
        u.UpdatedAt = _time.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        if (_cutoffs is not null &&
            (u.Role != oldRole || u.Permission != oldPermission ||
             !string.Equals(u.BasePath, oldBasePath, StringComparison.Ordinal)))
        {
            _cutoffs.InvalidateUser(id, _time.GetUtcNow());
        }
        return Resp.Ok();
    }

    /// <summary>
    /// 删除用户。仅内置管理员（<see cref="User.IsBuiltIn"/>）受保护不可删除 ——
    /// 它是系统唯一保证存在的管理入口；其余账号（含后来手工创建的管理员）均可删除。
    /// 删掉最后一个管理员仍允许（与 Go 版一致，Go 只保护内置账号不建模），但内置账号
    /// 本身保证系统不会彻底失去管理入口。
    /// </summary>
    public async Task<Resp> DeleteAsync(long id, CancellationToken ct = default)
    {
        var u = await _db.Users.FindAsync([id], ct);
        if (u is null) return Resp.Fail("user not found", RespCodes.NotFound);
        if (IsSuperAdmin(u))
            return Resp.Fail("cannot delete built-in admin", RespCodes.Forbidden);
        _db.Users.Remove(u);
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    /// <summary>
    /// 重置内置管理员的密码，供桌面端与恢复场景使用。
    /// 与 <see cref="UpdateAsync"/> 的区别是：无需调用者具备该账号身份，也无需旧密码 ——
    /// 因为内置管理员被刻意设计成不可删除、不可改名、不可降权，若同时要求旧密码，
    /// 忘记密码就彻底失去管理入口。密码为空时直接拒绝，避免把账号锁死。
    /// </summary>
    /// <param name="newPassword">新密码明文；为空或空白则返回 400。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回 <see cref="RespCodes.Ok"/>；系统未初始化返回 404。</returns>
    public async Task<Resp> ResetAdminPasswordAsync(string newPassword, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newPassword))
            return Resp.Fail("password required", RespCodes.BadRequest);

        // 判据必须带上 Role == Admin：guest 同样带 IsBuiltIn 标记，只按标记查会命中游客
        // 并改掉它的密码。尚未回填的旧库退回到"最早的管理员"。
        var admin = await _db.Users.FirstOrDefaultAsync(u => u.IsBuiltIn && u.Role == UserRole.Admin, ct)
                    ?? await _db.Users.Where(u => u.Role == UserRole.Admin)
                                      .OrderBy(u => u.Id)
                                      .FirstOrDefaultAsync(ct);
        if (admin is null)
            return Resp.Fail("admin not found", RespCodes.NotFound);

        admin.PasswordHash = HashPassword(newPassword);
        // 改密即解除锁定：忘记密码时管理员可能已被禁用。
        admin.Disabled = false;
        admin.UpdatedAt = _time.GetUtcNow();
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
        // 前端据此禁用"删除"按钮 —— 漏映射会让内置账号看起来可删。
        IsBuiltIn = u.IsBuiltIn,
        CreatedAt = u.CreatedAt,
        UpdatedAt = u.UpdatedAt,
    };
}
