using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 账号查询、资料更新、初始管理员创建与密码重置。密码使用 SHA256 加盐哈希（带固定后缀）。
/// 单用户产品：库里通常只有一个内置管理员，因此只有只读列表与按 id 更新，没有新建 / 删除接口。
/// 对应 Go 参考实现 <c>internal/op/user.go</c>。
/// </summary>
public sealed class UserService : IUserService
{
    private readonly OpenListDbContext _db;
    private readonly TimeProvider _time;
    private readonly UserTokenCutoffRegistry? _cutoffs;

    /// <summary>
    /// 创建用户服务实例。
    /// </summary>
    /// <param name="db">EF Core 数据库上下文。</param>
    /// <param name="time">时间源（可选，默认系统时区）。</param>
    /// <param name="cutoffs">token 失效登记表（可选；禁用状态变更时用来即时失效既有 token）。</param>
    public UserService(OpenListDbContext db, TimeProvider? time = null,
        UserTokenCutoffRegistry? cutoffs = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
        _cutoffs = cutoffs;
    }

    /// <summary>
    /// 按 Id 获取用户。
    /// </summary>
    public async Task<Resp<User>> GetAsync(long id, CancellationToken ct = default)
    {
        var u = await _db.Users.FindAsync([id], ct);
        return u is null ? Resp<User>.Fail("user not found", RespCodes.NotFound) : Resp<User>.Ok(u);
    }

    /// <summary>
    /// 列出全部账号（按 Id 升序）。对外只暴露 <see cref="UserPublic"/>：
    /// <see cref="User"/> 上带着 PasswordHash / TwoFactorSecret，直接序列化出去等于把
    /// 哈希与两步验证密钥一起发给浏览器。
    /// </summary>
    public async Task<Resp<List<UserPublic>>> ListAsync(CancellationToken ct = default)
    {
        var list = await _db.Users.AsNoTracking()
            .OrderBy(u => u.Id)
            .ToListAsync(ct);
        return Resp<List<UserPublic>>.Ok(list.Select(ToPublic).ToList());
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
    /// 获取系统中的唯一账号（按 Id 升序的第一条）。供初始化向导判断系统是否已初始化。
    /// 对应 Go <c>op.GetAdmin</c>：无账号时返回 404（对应 gorm.ErrRecordNotFound）。
    /// </summary>
    public async Task<Resp<User>> GetAdminAsync(CancellationToken ct = default)
    {
        var u = await _db.Users.AsNoTracking()
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
    /// 创建系统的初始管理员（Web 安装向导用）。用户表非空说明系统已初始化，
    /// 此时拒绝，避免向导被重复调用来追加账号。
    /// </summary>
    public async Task<Resp<UserPublic>> CreateInitialAdminAsync(
        string username, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return Resp<UserPublic>.Fail("username and password required", RespCodes.BadRequest);

        if (await _db.Users.AnyAsync(ct))
            return Resp<UserPublic>.Fail("system has already been initialized", RespCodes.BadRequest);

        var now = _time.GetUtcNow();
        var u = new User
        {
            Username = username,
            PasswordHash = HashPassword(password),
            Disabled = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Users.Add(u);
        await _db.SaveChangesAsync(ct);
        return Resp<UserPublic>.Ok(ToPublic(u));
    }

    /// <summary>
    /// 更新指定用户的信息。
    /// </summary>
    public async Task<Resp> UpdateAsync(long id, UpdateUserReq req, CancellationToken ct = default)
    {
        var u = await _db.Users.FindAsync([id], ct);
        if (u is null) return Resp.Fail("user not found", RespCodes.NotFound);

        // Disabled 会被鉴权链（UserTokenCutoffRegistry）用来即时失效既有 token：
        // 禁用后旧 token 若不失效，被禁账号仍能继续操作到过期。
        var oldDisabled = u.Disabled;

        if (!string.IsNullOrWhiteSpace(req.Username)) u.Username = req.Username;
        if (!string.IsNullOrWhiteSpace(req.Password)) u.PasswordHash = HashPassword(req.Password);
        if (req.Disabled.HasValue) u.Disabled = req.Disabled.Value;
        if (req.TwoFactorEnabled.HasValue) u.TwoFactorEnabled = req.TwoFactorEnabled.Value;
        if (req.AllowLdap.HasValue) u.AllowLdap = req.AllowLdap.Value;
        // 空串而非 null：旧库 SsoID 列是 INTEGER NOT NULL，写 null 会让保存抛
        // SQLite Error 19（见 User.SsoID 注释）。空串即"未绑定"。
        if (req.SsoID is not null) u.SsoID = string.IsNullOrWhiteSpace(req.SsoID) ? string.Empty : req.SsoID;
        u.UpdatedAt = _time.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        if (_cutoffs is not null && u.Disabled != oldDisabled)
            _cutoffs.InvalidateUser(id, _time.GetUtcNow());
        return Resp.Ok();
    }

    /// <summary>
    /// 重置管理员密码，供桌面端与恢复场景使用。
    /// 与 <see cref="UpdateAsync"/> 的区别是：无需调用者具备该账号身份，也无需旧密码 ——
    /// 账号被刻意设计成不可删除，若同时要求旧密码，忘记密码就彻底失去管理入口。
    /// 密码为空时直接拒绝，避免把账号锁死。
    /// </summary>
    /// <param name="newPassword">新密码明文；为空或空白则返回 400。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回 <see cref="RespCodes.Ok"/>；系统未初始化返回 404。</returns>
    public async Task<Resp> ResetAdminPasswordAsync(string newPassword, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newPassword))
            return Resp.Fail("password required", RespCodes.BadRequest);

        var admin = await _db.Users.OrderBy(u => u.Id).FirstOrDefaultAsync(ct);
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
        Disabled = u.Disabled,
        TwoFactorEnabled = u.TwoFactorEnabled,
        SsoID = u.SsoID,
        AllowLdap = u.AllowLdap,
        CreatedAt = u.CreatedAt,
        UpdatedAt = u.UpdatedAt,
    };
}
