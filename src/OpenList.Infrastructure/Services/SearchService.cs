using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

public class SearchService : ISearchService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, List<SearchResult>> _cache = new();
    private IndexProgress _progress = new() { State = "idle" };

    public SearchService(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task BuildIndexAsync(CancellationToken ct = default)
    {
        _progress = new IndexProgress { State = "building", Progress = 0 };
        _ = Task.Run(async () =>
        {
            await Task.Delay(100, ct);
            _progress = new IndexProgress { State = "idle", Progress = 100 };
        }, ct);
        await Task.CompletedTask;
    }

    public async Task UpdateIndexAsync(string parent, List<IObj> objs, CancellationToken ct = default)
    {
        var results = objs.Select(o => new SearchResult
        {
            Parent = parent,
            Name = o.GetName(),
            IsDir = o.IsDir(),
            Size = o.GetSize(),
            Modified = o.GetModTime()
        }).ToList();

        _cache.AddOrUpdate(parent, results, (_, _) => results);

        // Persist to database
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();
        var existing = await db.SearchIndices.Where(s => s.Parent == parent).ToListAsync(ct);
        db.SearchIndices.RemoveRange(existing);

        db.SearchIndices.AddRange(results.Select(r => new SearchIndex
        {
            Parent = r.Parent,
            Name = r.Name,
            IsDir = r.IsDir,
            Size = r.Size,
            Modified = r.Modified
        }));

        await db.SaveChangesAsync(ct);
    }

    public async Task<List<SearchResult>> SearchAsync(string parent, string keywords, int page, int perPage, CancellationToken ct = default)
    {
        // Try cache first
        if (_cache.TryGetValue(parent, out var cached))
        {
            var filtered = cached.Where(r => r.Name.Contains(keywords, StringComparison.OrdinalIgnoreCase));
            return filtered.Skip((page - 1) * perPage).Take(perPage).ToList();
        }

        // Fall back to database
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();
        var query = db.SearchIndices.AsQueryable();
        if (!string.IsNullOrEmpty(parent))
            query = query.Where(s => s.Parent == parent);

        var results = await query
            .Where(s => s.Name.Contains(keywords))
            .OrderBy(s => s.Name)
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .Select(s => new SearchResult
            {
                Parent = s.Parent,
                Name = s.Name,
                IsDir = s.IsDir,
                Size = s.Size,
                Modified = s.Modified
            })
            .ToListAsync(ct);

        return results;
    }

    public Task StopIndexAsync(CancellationToken ct = default)
    {
        _progress = new IndexProgress { State = "stopped" };
        return Task.CompletedTask;
    }

    public async Task ClearIndexAsync(CancellationToken ct = default)
    {
        _cache.Clear();
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();
        db.SearchIndices.RemoveRange(db.SearchIndices);
        await db.SaveChangesAsync(ct);
    }

    public Task<IndexProgress> GetProgressAsync(CancellationToken ct = default)
        => Task.FromResult(_progress);
}
