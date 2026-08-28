using Microsoft.EntityFrameworkCore;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 路径元数据增删改查：密码保护、写权限、readme、隐藏、WebDAV 策略等。
/// 对应 Go 参考实现 <c>internal/op/meta.go</c>。
/// </summary>
public sealed class MetaService : IMetaService
{
    private readonly OpenListDbContext _db;

    /// <summary>
    /// 创建元数据服务实例。
    /// </summary>
    public MetaService(OpenListDbContext db) => _db = db;

    /// <summary>
    /// 列出全部路径元数据（按 Path 升序）。
    /// </summary>
    public async Task<Resp<List<Meta>>> ListAsync(CancellationToken ct = default)
    {
        var list = await _db.Metas.AsNoTracking().OrderBy(m => m.Path).ToListAsync(ct);
        foreach (var m in list) m.Hydrate();
        return Resp<List<Meta>>.Ok(list);
    }

    /// <summary>
    /// 按路径获取元数据。
    /// </summary>
    public async Task<Resp<Meta>> GetAsync(string path, CancellationToken ct = default)
    {
        var meta = await _db.Metas.AsNoTracking().FirstOrDefaultAsync(m => m.Path == path, ct);
        if (meta is null) return Resp<Meta>.Fail("meta not found", RespCodes.NotFound);
        meta.Hydrate();
        return Resp<Meta>.Ok(meta);
    }

    /// <summary>
    /// 按 Id 获取元数据。
    /// </summary>
    public async Task<Resp<Meta>> GetByIdAsync(long id, CancellationToken ct = default)
    {
        var meta = await _db.Metas.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
        if (meta is null) return Resp<Meta>.Fail("meta not found", RespCodes.NotFound);
        meta.Hydrate();
        return Resp<Meta>.Ok(meta);
    }

    /// <summary>
    /// 创建元数据：校验路径非空且唯一后入库。
    /// </summary>
    public async Task<Resp<Meta>> CreateAsync(Meta meta, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(meta.Path))
            return Resp<Meta>.Fail("path is required", RespCodes.BadRequest);

        if (await _db.Metas.AnyAsync(m => m.Path == meta.Path, ct))
            return Resp<Meta>.Fail("meta already exists", RespCodes.Conflict);

        meta.SyncJson();
        _db.Metas.Add(meta);
        await _db.SaveChangesAsync(ct);
        return Resp<Meta>.Ok(meta);
    }

    /// <summary>
    /// 更新指定 Id 的元数据（覆盖所有字段并同步 JSON 列）。
    /// </summary>
    public async Task<Resp> UpdateAsync(long id, Meta meta, CancellationToken ct = default)
    {
        var existing = await _db.Metas.FindAsync([id], ct);
        if (existing is null) return Resp.Fail("meta not found", RespCodes.NotFound);

        existing.Path = meta.Path;
        existing.Password = meta.Password;
        existing.PSub = meta.PSub;
        existing.ReadUsers = meta.ReadUsers;
        existing.ReadUsersSub = meta.ReadUsersSub;
        existing.WriteUsers = meta.WriteUsers;
        existing.WriteUsersSub = meta.WriteUsersSub;
        existing.Write = meta.Write;
        existing.WSub = meta.WSub;
        existing.Header = meta.Header;
        existing.HeaderSub = meta.HeaderSub;
        existing.Readme = meta.Readme;
        existing.RSub = meta.RSub;
        existing.Hide = meta.Hide;
        existing.HideSub = meta.HideSub;
        existing.SyncJson();

        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    /// <summary>
    /// 删除指定 Id 的元数据。
    /// </summary>
    public async Task<Resp> DeleteAsync(long id, CancellationToken ct = default)
    {
        var meta = await _db.Metas.FindAsync([id], ct);
        if (meta is null) return Resp.Fail("meta not found", RespCodes.NotFound);
        _db.Metas.Remove(meta);
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }
}
