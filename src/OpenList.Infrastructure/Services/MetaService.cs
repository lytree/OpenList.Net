using Microsoft.EntityFrameworkCore;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// Path metadata CRUD: password protection, write permissions, readme, hide, WebDAV policies.
/// Mirrors OpenList Go <c>internal/op/meta.go</c>.
/// </summary>
public sealed class MetaService : IMetaService
{
    private readonly OpenListDbContext _db;

    public MetaService(OpenListDbContext db) => _db = db;

    public async Task<Resp<List<Meta>>> ListAsync(CancellationToken ct = default)
    {
        var list = await _db.Metas.AsNoTracking().OrderBy(m => m.Path).ToListAsync(ct);
        return Resp<List<Meta>>.Ok(list);
    }

    public async Task<Resp<Meta>> GetAsync(string path, CancellationToken ct = default)
    {
        var meta = await _db.Metas.AsNoTracking().FirstOrDefaultAsync(m => m.Path == path, ct);
        return meta is null
            ? Resp<Meta>.Fail("meta not found", RespCodes.NotFound)
            : Resp<Meta>.Ok(meta);
    }

    public async Task<Resp<Meta>> CreateAsync(Meta meta, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(meta.Path))
            return Resp<Meta>.Fail("path is required", RespCodes.BadRequest);

        if (await _db.Metas.AnyAsync(m => m.Path == meta.Path, ct))
            return Resp<Meta>.Fail("meta already exists", RespCodes.Conflict);

        _db.Metas.Add(meta);
        await _db.SaveChangesAsync(ct);
        return Resp<Meta>.Ok(meta);
    }

    public async Task<Resp> UpdateAsync(long id, Meta meta, CancellationToken ct = default)
    {
        var existing = await _db.Metas.FindAsync([id], ct);
        if (existing is null) return Resp.Fail("meta not found", RespCodes.NotFound);

        existing.Path = meta.Path;
        existing.Password = meta.Password;
        existing.PSub = meta.PSub;
        existing.Write = meta.Write;
        existing.Header = meta.Header;
        existing.Readme = meta.Readme;
        existing.Hide = meta.Hide;
        existing.WebdavPolicy = meta.WebdavPolicy;
        existing.WebdavRead = meta.WebdavRead;
        existing.WebdavWrite = meta.WebdavWrite;
        existing.WebdavManagement = meta.WebdavManagement;

        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    public async Task<Resp> DeleteAsync(long id, CancellationToken ct = default)
    {
        var meta = await _db.Metas.FindAsync([id], ct);
        if (meta is null) return Resp.Fail("meta not found", RespCodes.NotFound);
        _db.Metas.Remove(meta);
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }
}
