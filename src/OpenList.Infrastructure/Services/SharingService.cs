using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 分享管理服务。对应 Go 参考实现 <c>internal/op/sharing.go</c> +
/// <c>server/handles/sharing.go</c>。
/// </summary>
public sealed class SharingService : ISharingService
{
    private readonly OpenListDbContext _db;
    private readonly TimeProvider _time;

    /// <summary>
    /// 创建分享服务实例。
    /// </summary>
    /// <param name="db">EF Core 数据库上下文。</param>
    /// <param name="time">时间源（可选，默认系统时区）。</param>
    public SharingService(OpenListDbContext db, TimeProvider? time = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// 列出分享（管理员可见全部，普通用户仅可见自己的），按 Id 倒序。
    /// </summary>
    public async Task<Resp<List<ShareInfo>>> ListAsync(long userId, bool isAdmin, CancellationToken ct = default)
    {
        var q = _db.Sharings.AsNoTracking();
        if (!isAdmin) q = q.Where(s => s.OwnerId == userId);
        var list = await q.OrderByDescending(s => s.Id).ToListAsync(ct);
        var users = await GetOwnersAsync(list, ct);
        return Resp<List<ShareInfo>>.Ok(list.ConvertAll(s => ToInfo(s, users)));
    }

    /// <summary>
    /// 按分享码获取分享（普通用户不能读取他人的分享）。
    /// </summary>
    public async Task<Resp<ShareInfo>> GetAsync(string code, long userId, bool isAdmin, CancellationToken ct = default)
    {
        var s = await _db.Sharings.AsNoTracking().FirstOrDefaultAsync(x => x.ShareCode == code, ct);
        if (s is null || (!isAdmin && s.OwnerId != userId))
            return Resp<ShareInfo>.Fail("sharing not found", RespCodes.NotFound);
        var users = await GetOwnersAsync([s], ct);
        return Resp<ShareInfo>.Ok(ToInfo(s, users));
    }

    /// <summary>
    /// 创建分享：校验至少一个对象、解析所有者（管理员可指定创建者）、生成/校验分享码后入库。
    /// </summary>
    public async Task<Resp<ShareInfo>> CreateAsync(ShareReq req, long userId, bool isAdmin, CancellationToken ct = default)
    {
        if (req.Files.Count == 0 || (req.Files.Count == 1 && string.IsNullOrEmpty(req.Files[0])))
            return Resp<ShareInfo>.Fail("must add at least 1 object", RespCodes.BadRequest);

        var owner = await ResolveOwnerAsync(req.CreatorName, userId, isAdmin, ct);
        if (owner is null) return Resp<ShareInfo>.Fail("no such a user", RespCodes.BadRequest);

        // 非管理员不能自定义分享码。
        var code = string.IsNullOrWhiteSpace(req.ShareCode) ? GenerateCode() : req.ShareCode;
        if (!isAdmin && !string.IsNullOrWhiteSpace(req.ShareCode))
            return Resp<ShareInfo>.Fail("permission denied", RespCodes.Forbidden);

        var now = _time.GetUtcNow();
        var sharing = new Sharing
        {
            ShareCode = code,
            Path = req.Files.FirstOrDefault() ?? string.Empty,
            FilesJson = JsonSerializer.Serialize(req.Files),
            Password = req.Password,
            Expiration = req.Expiration,
            MaxAccessed = req.MaxAccessed,
            Accessed = 0,
            Disabled = req.Disabled,
            Remark = req.Remark,
            Readme = req.Readme,
            Header = req.Header,
            OrderBy = req.OrderBy,
            OrderDirection = req.OrderDirection,
            ExtractFolder = req.ExtractFolder,
            OwnerId = owner.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Sharings.Add(sharing);
        await _db.SaveChangesAsync(ct);
        return Resp<ShareInfo>.Ok(ToInfo(sharing, new Dictionary<long, User> { [owner.Id] = owner }));
    }

    /// <summary>
    /// 更新分享信息（文件、密码、过期时间、访问上限、备注等）。
    /// </summary>
    public async Task<Resp<ShareInfo>> UpdateAsync(string code, ShareReq req, long userId, bool isAdmin, CancellationToken ct = default)
    {
        var s = await _db.Sharings.FirstOrDefaultAsync(x => x.ShareCode == code, ct);
        if (s is null || (!isAdmin && s.OwnerId != userId))
            return Resp<ShareInfo>.Fail("sharing not found", RespCodes.NotFound);

        if (!isAdmin && !string.IsNullOrWhiteSpace(req.NewShareCode))
            return Resp<ShareInfo>.Fail("permission denied", RespCodes.Forbidden);

        if (req.Files.Count > 0) { s.FilesJson = JsonSerializer.Serialize(req.Files); s.Path = req.Files[0]; }
        s.Password = req.Password;
        s.Expiration = req.Expiration;
        s.MaxAccessed = req.MaxAccessed;
        s.Accessed = req.Accessed;
        s.Disabled = req.Disabled;
        s.Remark = req.Remark;
        s.Readme = req.Readme;
        s.Header = req.Header;
        s.OrderBy = req.OrderBy;
        s.OrderDirection = req.OrderDirection;
        s.ExtractFolder = req.ExtractFolder;
        if (!string.IsNullOrWhiteSpace(req.NewShareCode)) s.ShareCode = req.NewShareCode;
        s.UpdatedAt = _time.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        var users = await GetOwnersAsync([s], ct);
        return Resp<ShareInfo>.Ok(ToInfo(s, users));
    }

    /// <summary>
    /// 删除分享（非管理员不能删除他人的分享）。
    /// </summary>
    public async Task<Resp> DeleteAsync(string code, long userId, bool isAdmin, CancellationToken ct = default)
    {
        var s = await _db.Sharings.FirstOrDefaultAsync(x => x.ShareCode == code, ct);
        if (s is null) return Resp.Fail("sharing not found", RespCodes.NotFound);
        if (!isAdmin && s.OwnerId != userId) return Resp.Fail("permission denied", RespCodes.Forbidden);
        _db.Sharings.Remove(s);
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    /// <summary>
    /// 启用分享。
    /// </summary>
    public async Task<Resp> EnableAsync(string code, long userId, bool isAdmin, CancellationToken ct = default)
        => await SetDisabledAsync(code, false, userId, isAdmin, ct);

    /// <summary>
    /// 禁用分享。
    /// </summary>
    public async Task<Resp> DisableAsync(string code, long userId, bool isAdmin, CancellationToken ct = default)
        => await SetDisabledAsync(code, true, userId, isAdmin, ct);

    /// <summary>
    /// 校验分享可访问性：检查分享存在、未被禁用、未过期、密码正确且未超过访问上限，
    /// 通过后递增访问计数并返回分享实体。
    /// </summary>
    public async Task<Resp<Sharing>> VerifyAsync(string code, string? password, CancellationToken ct = default)
    {
        var s = await _db.Sharings.FirstOrDefaultAsync(x => x.ShareCode == code, ct);
        if (s is null) return Resp<Sharing>.Fail("sharing not found", RespCodes.NotFound);
        if (s.Disabled) return Resp<Sharing>.Fail("sharing disabled", RespCodes.Forbidden);
        if (s.Expiration is { } exp && exp < _time.GetUtcNow())
            return Resp<Sharing>.Fail("sharing expired", RespCodes.Forbidden);
        if (!string.IsNullOrEmpty(s.Password) && s.Password != password)
            return Resp<Sharing>.Fail("wrong password", RespCodes.Forbidden);
        if (s.MaxAccessed > 0 && s.Accessed >= s.MaxAccessed)
            return Resp<Sharing>.Fail("sharing max accessed", RespCodes.Forbidden);
        // 递增访问计数（对应 Go server/handles/sharing.go 的 DownShare 中递增 Visits）。
        s.Accessed += 1;
        s.UpdatedAt = _time.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return Resp<Sharing>.Ok(s);
    }

    /// <summary>
    /// 设置分享的启用/禁用状态（非管理员不能操作他人分享）。
    /// </summary>
    private async Task<Resp> SetDisabledAsync(string code, bool disabled, long userId, bool isAdmin, CancellationToken ct)
    {
        var s = await _db.Sharings.FirstOrDefaultAsync(x => x.ShareCode == code, ct);
        if (s is null) return Resp.Fail("sharing not found", RespCodes.NotFound);
        if (!isAdmin && s.OwnerId != userId) return Resp.Fail("permission denied", RespCodes.Forbidden);
        s.Disabled = disabled;
        s.UpdatedAt = _time.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    /// <summary>
    /// 解析分享所有者：管理员可指定创建者用户名，否则取请求用户自身。
    /// </summary>
    private async Task<User?> ResolveOwnerAsync(string? creatorName, long userId, bool isAdmin, CancellationToken ct)
    {
        if (isAdmin && !string.IsNullOrWhiteSpace(creatorName))
            return await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == creatorName, ct);
        return await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
    }

    /// <summary>
    /// 一次性批量查询给定分享列表的所有者用户，返回 Id → User 映射。
    /// </summary>
    private async Task<Dictionary<long, User>> GetOwnersAsync(List<Sharing> list, CancellationToken ct)
    {
        var ids = list.Select(s => s.OwnerId).Distinct().ToList();
        if (ids.Count == 0) return new();
        var users = await _db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToListAsync(ct);
        return users.ToDictionary(u => u.Id);
    }

    private static ShareInfo ToInfo(Sharing s, Dictionary<long, User> owners)
    {
        var owner = owners.GetValueOrDefault(s.OwnerId);
        return new ShareInfo
        {
            Id = s.ShareCode,
            Files = ParseFiles(s.FilesJson),
            Expires = s.Expiration,
            Pwd = s.Password,
            MaxAccessed = s.MaxAccessed,
            Accessed = s.Accessed,
            Disabled = s.Disabled,
            Remark = s.Remark,
            Readme = s.Readme,
            Header = s.Header,
            OrderBy = s.OrderBy,
            OrderDirection = s.OrderDirection,
            ExtractFolder = s.ExtractFolder,
            Creator = owner?.Username ?? string.Empty,
            CreatorRole = (int)(owner?.Role ?? UserRole.General),
        };
    }

    private static List<string> ParseFiles(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }

    private static string GenerateCode() => Guid.NewGuid().ToString("N")[..8];
}
