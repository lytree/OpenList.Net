using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

public class MetaService : IMetaService
{
    private readonly OpenListDbContext _db;

    public MetaService(OpenListDbContext db) => _db = db;

    public async Task<(List<Meta> Metas, int Total)> ListMetasAsync(int page = 1, int perPage = 100, CancellationToken ct = default)
    {
        var total = await _db.Metas.CountAsync(ct);
        var metas = await _db.Metas.Skip((page - 1) * perPage).Take(perPage).ToListAsync(ct);
        return (metas, total);
    }

    public async Task<Meta?> GetMetaAsync(string path, CancellationToken ct = default)
        => await _db.Metas.FirstOrDefaultAsync(m => m.Path == path, ct);

    public async Task<Meta?> GetMetaByIdAsync(uint id, CancellationToken ct = default)
        => await _db.Metas.FindAsync([id], ct);

    public async Task<Meta?> GetNearestMetaAsync(string path, CancellationToken ct = default)
    {
        // Find the nearest meta that covers the given path
        var metas = await _db.Metas.ToListAsync(ct);
        return metas
            .Where(m => PathCoversMeta(m.Path, path, m.PSub))
            .OrderByDescending(m => m.Path.Length)
            .FirstOrDefault();
    }

    public async Task CreateMetaAsync(Meta meta, CancellationToken ct = default)
    {
        _db.Metas.Add(meta);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateMetaAsync(Meta meta, CancellationToken ct = default)
    {
        _db.Metas.Update(meta);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteMetaAsync(string path, CancellationToken ct = default)
    {
        var meta = await _db.Metas.FirstOrDefaultAsync(m => m.Path == path, ct);
        if (meta != null)
        {
            _db.Metas.Remove(meta);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task DeleteMetaByIdAsync(uint id, CancellationToken ct = default)
    {
        var meta = await _db.Metas.FindAsync([id], ct);
        if (meta != null)
        {
            _db.Metas.Remove(meta);
            await _db.SaveChangesAsync(ct);
        }
    }

    public static bool PathCoversMeta(string metaPath, string targetPath, bool sub)
    {
        if (metaPath == targetPath) return true;
        if (!sub) return false;
        return targetPath.StartsWith(metaPath + "/", StringComparison.OrdinalIgnoreCase);
    }
}
