using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

public class SharingService : ISharingService
{
    private readonly OpenListDbContext _db;
    private readonly IFileSystemService _fs;

    public SharingService(OpenListDbContext db, IFileSystemService fs) { _db = db; _fs = fs; }

    public async Task<(List<Sharing> Sharings, int Total)> ListSharingsAsync(uint userId, int page = 1, int perPage = 100, CancellationToken ct = default)
    {
        var query = userId == 0
            ? _db.Sharings.AsQueryable()
            : _db.Sharings.Where(s => s.CreatorId == userId);
        var total = await query.CountAsync(ct);
        var sharings = await query.OrderByDescending(s => s.Id)
            .Skip((page - 1) * perPage).Take(perPage).ToListAsync(ct);
        return (sharings, total);
    }

    public async Task<Sharing?> GetSharingAsync(string id, CancellationToken ct = default)
        => await _db.Sharings.FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<Sharing> CreateSharingAsync(Sharing sharing, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sharing.Id))
            sharing.Id = TaskService.GenerateNanoId(8);
        _db.Sharings.Add(sharing);
        await _db.SaveChangesAsync(ct);
        return sharing;
    }

    public async Task UpdateSharingAsync(Sharing sharing, CancellationToken ct = default)
    {
        _db.Sharings.Update(sharing);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateSharingIdAsync(string oldId, string newId, CancellationToken ct = default)
    {
        var sharing = await _db.Sharings.FindAsync([oldId], ct);
        if (sharing == null) return;
        _db.Sharings.Remove(sharing);
        sharing.Id = newId;
        _db.Sharings.Add(sharing);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteSharingAsync(string id, CancellationToken ct = default)
    {
        var sharing = await _db.Sharings.FindAsync([id], ct);
        if (sharing != null)
        {
            _db.Sharings.Remove(sharing);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task EnableSharingAsync(string id, CancellationToken ct = default)
    {
        var sharing = await _db.Sharings.FindAsync([id], ct);
        if (sharing != null)
        {
            sharing.Disabled = false;
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task DisableSharingAsync(string id, CancellationToken ct = default)
    {
        var sharing = await _db.Sharings.FindAsync([id], ct);
        if (sharing != null)
        {
            sharing.Disabled = true;
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task<(Sharing? sharing, List<IObj> objs)> ListSharingObjectsAsync(string sid, string path, string? pwd = null, bool refresh = false, CancellationToken ct = default)
    {
        var sharing = await _db.Sharings.FindAsync([sid], ct);
        if (sharing == null || !sharing.Valid() || !sharing.Verify(pwd))
            return (sharing, []);

        var files = sharing.Files ?? [];
        path = path.TrimStart('/');

        if (files.Count == 1 || !string.IsNullOrEmpty(path))
        {
            // Navigate into a shared directory
            var basePath = files.Count == 1 ? files[0] : "";
            var fullPath = string.IsNullOrEmpty(path) ? basePath : $"{basePath}/{path}".Trim('/');
            try
            {
                var objs = await _fs.ListAsync($"/{fullPath}", new ListArgs { Refresh = refresh }, ct);
                return (sharing, objs);
            }
            catch
            {
                return (sharing, []);
            }
        }

        // List root shared files
        var result = new List<IObj>();
        foreach (var f in files.Where(f => f != "/"))
        {
            try
            {
                var obj = await _fs.GetAsync(f, ct);
                if (obj != null) result.Add(obj);
            }
            catch { /* skip inaccessible files */ }
        }
        return (sharing, result);
    }

    public async Task<(Sharing? sharing, IObj? obj)> GetSharingObjectAsync(string sid, string path, string? pwd = null, CancellationToken ct = default)
    {
        var sharing = await _db.Sharings.FindAsync([sid], ct);
        if (sharing == null || !sharing.Valid() || !sharing.Verify(pwd))
            return (sharing, null);

        var files = sharing.Files ?? [];
        path = path.TrimStart('/');

        var basePath = files.Count == 1 ? files[0] : "";
        var fullPath = string.IsNullOrEmpty(path) ? basePath : $"{basePath}/{path}".Trim('/');

        try
        {
            var obj = await _fs.GetAsync($"/{fullPath}", ct);
            return (sharing, obj);
        }
        catch
        {
            return (sharing, null);
        }
    }

    // Go: countAccess - per-IP dedup with cache, increment Accessed
    private static readonly ConcurrentDictionary<string, byte> _accessCache = new();
    public async Task IncrementAccessAsync(string id, string ip, CancellationToken ct = default)
    {
        var cacheKey = $"{id}:{ip}";
        if (_accessCache.TryAdd(cacheKey, 0))
        {
            var sharing = await _db.Sharings.FindAsync([id], ct);
            if (sharing != null)
            {
                sharing.Accessed++;
                await _db.SaveChangesAsync(ct);
            }
            // Auto-remove cache entry after delay (Go: AccessCountDelay)
            _ = Task.Delay(TimeSpan.FromMinutes(5)).ContinueWith(_ => _accessCache.TryRemove(cacheKey, out var _));
        }
    }
}


