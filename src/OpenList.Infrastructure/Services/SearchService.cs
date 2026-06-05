using System.Collections.Concurrent;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.Services;

public class SearchService : ISearchService
{
    private readonly ConcurrentDictionary<string, List<SearchResult>> _index = new();
    private IndexProgress _progress = new() { State = "idle" };

    public Task BuildIndexAsync(CancellationToken ct = default)
    {
        _progress = new IndexProgress { State = "building", Progress = 0 };
        // Full index build would enumerate all storage paths
        _ = Task.Run(async () =>
        {
            await Task.Delay(100, ct);
            _progress = new IndexProgress { State = "idle", Progress = 100 };
        }, ct);
        return Task.CompletedTask;
    }

    public Task UpdateIndexAsync(string parent, List<IObj> objs, CancellationToken ct = default)
    {
        var results = objs.Select(o => new SearchResult
        {
            Parent = parent,
            Name = o.GetName(),
            IsDir = o.IsDir(),
            Size = o.GetSize(),
            Modified = o.GetModTime()
        }).ToList();

        _index.AddOrUpdate(parent, results, (_, _) => results);
        return Task.CompletedTask;
    }

    public Task<List<SearchResult>> SearchAsync(string parent, string keywords, int page, int perPage, CancellationToken ct = default)
    {
        var results = new List<SearchResult>();
        foreach (var entry in _index)
        {
            results.AddRange(entry.Value.Where(r =>
                r.Name.Contains(keywords, StringComparison.OrdinalIgnoreCase)));
        }
        return Task.FromResult(results.Skip((page - 1) * perPage).Take(perPage).ToList());
    }

    public Task StopIndexAsync(CancellationToken ct = default)
    {
        _progress = new IndexProgress { State = "stopped" };
        return Task.CompletedTask;
    }

    public Task ClearIndexAsync(CancellationToken ct = default)
    {
        _index.Clear();
        return Task.CompletedTask;
    }

    public Task<IndexProgress> GetProgressAsync(CancellationToken ct = default)
        => Task.FromResult(_progress);
}
