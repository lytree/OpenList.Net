using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// Search index service. Walks the filesystem via <see cref="IFileSystemService"/>
/// and persists entries to the <c>x_search_index</c> table (the "database" index
/// backend). Query dispatch is driven by the <c>search_index</c> setting:
/// <list type="bullet">
/// <item><c>none</c> — index not used; <see cref="SearchAsync"/> delegates to the live filesystem.</item>
/// <item><c>database</c> — <see cref="SearchAsync"/> queries the persisted rows.</item>
/// <item><c>bleve</c> — not implemented; falls back to filesystem search.</item>
/// </list>
/// Mirrors OpenList Go <c>server/handles/search.go</c>.
/// </summary>
public sealed class SearchService : ISearchService
{
    private readonly IFileSystemService _fs;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private IndexProgress _progress = new();
    private CancellationTokenSource? _cts;
    private DateTimeOffset? _lastDoneTime;

    public SearchService(IFileSystemService fs, IServiceScopeFactory scopes, TimeProvider? time = null)
    {
        _fs = fs;
        _scopes = scopes;
        _time = time ?? TimeProvider.System;
    }

    public Task<Resp> BuildIndexAsync(IReadOnlyList<string> paths, int maxDepth, CancellationToken ct = default)
        => StartAsync(paths, maxDepth, isUpdate: false, ct);

    public Task<Resp> UpdateIndexAsync(IReadOnlyList<string> paths, int maxDepth, CancellationToken ct = default)
        => StartAsync(paths, maxDepth, isUpdate: true, ct);

    private Task<Resp> StartAsync(IReadOnlyList<string> paths, int maxDepth, bool isUpdate, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_progress.State == "running")
                return Task.FromResult(Resp.Fail("index build already running", RespCodes.Conflict));

            _cts = new CancellationTokenSource();
            _progress = new IndexProgress
            {
                State = "running",
                StartedAt = _time.GetUtcNow(),
                // Carry forward last-done time so progress endpoint stays consistent.
                LastDoneTime = _lastDoneTime,
            };
        }

        var effectivePaths = paths.Count == 0 ? new[] { "/" } : paths;
        _ = Task.Run(() => IndexAsync(effectivePaths, maxDepth, isUpdate, _cts.Token), CancellationToken.None);

        return Task.FromResult(Resp.Ok());
    }

    public Task<Resp> StopIndexAsync(CancellationToken ct = default)
    {
        lock (_lock) { _cts?.Cancel(); }
        return Task.FromResult(Resp.Ok());
    }

    public async Task<Resp> ClearIndexAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _progress = new IndexProgress { State = "idle", LastDoneTime = _lastDoneTime };
        }
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();
        // Delete all rows in a single round-trip.
        await db.SearchIndex.ExecuteDeleteAsync(ct);
        return Resp.Ok();
    }

    public Task<Resp<IndexProgress>> GetProgressAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            var snapshot = new IndexProgress
            {
                State = _progress.State,
                Indexed = _progress.Indexed,
                Total = _progress.Total,
                StartedAt = _progress.StartedAt,
                FinishedAt = _progress.FinishedAt,
                LastDoneTime = _progress.LastDoneTime,
                Error = _progress.Error,
            };
            return Task.FromResult(Resp<IndexProgress>.Ok(snapshot));
        }
    }

    public async Task<Resp<SearchResp>> SearchAsync(SearchReq req, CancellationToken ct = default)
    {
        // Decide backend from the search_index setting.
        var backend = await GetSettingAsync("search_index", "none");
        if (!string.Equals(backend, "database", StringComparison.OrdinalIgnoreCase))
        {
            // none / bleve (not implemented) → live filesystem search.
            return await _fs.SearchAsync(req, new User { Id = 0, Username = "search", Role = UserRole.Admin }, ct);
        }

        return await SearchDatabaseAsync(req, ct);
    }

    private async Task<Resp<SearchResp>> SearchDatabaseAsync(SearchReq req, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();

        var parent = NormalizeParent(req.Parent);
        // SQLite LIKE is case-insensitive for ASCII by default; this matches Go's
        // database search behavior well enough for the common case.
        var kw = $"%{EscapeLike(req.Keywords)}%";
        var q = db.SearchIndex.AsNoTracking()
            .Where(e => EF.Functions.Like(e.Name, kw, "\\"));
        if (req.Recursive)
            q = q.Where(e => e.Parent == "/" || e.Parent.StartsWith(parent));
        else
            q = q.Where(e => e.Parent == parent);

        var total = await q.CountAsync(ct);
        var page = Math.Max(1, req.Page);
        var perPage = req.PerPage < 1 ? int.MaxValue : req.PerPage;
        var rows = await q.OrderByDescending(e => e.Id)
            .Skip((page - 1) * perPage).Take(perPage)
            .Select(e => new SearchNode
            {
                Parent = e.Parent,
                Name = e.Name,
                IsDir = e.IsDir,
                Size = e.Size,
                Path = e.Path,
                Type = e.Type,
            })
            .ToListAsync(ct);

        return Resp<SearchResp>.Ok(new SearchResp { Content = rows, Total = total });
    }

    private async Task IndexAsync(IReadOnlyList<string> paths, int maxDepth, bool isUpdate, CancellationToken ct)
    {
        try
        {
            var admin = new User { Id = 0, Username = "indexer", Role = UserRole.Admin };

            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();

            // For a full build, wipe the table first. For an update, we delete only
            // rows under the walked roots and re-insert fresh ones.
            if (!isUpdate)
            {
                await db.SearchIndex.ExecuteDeleteAsync(ct);
            }
            else
            {
                foreach (var root in paths)
                {
                    var normalized = NormalizeParent(root);
                    db.SearchIndex.RemoveRange(db.SearchIndex.Where(e => e.Parent == "/" || e.Parent.StartsWith(normalized)));
                }
                await db.SaveChangesAsync(ct);
            }

            foreach (var root in paths)
            {
                ct.ThrowIfCancellationRequested();
                await WalkAsync(root, 0, maxDepth, admin, db, ct);
            }

            lock (_lock)
            {
                _progress.State = "completed";
                _progress.FinishedAt = _time.GetUtcNow();
                _lastDoneTime = _progress.FinishedAt;
                _progress.LastDoneTime = _lastDoneTime;
            }
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                _progress.State = "cancelled";
                _progress.FinishedAt = _time.GetUtcNow();
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _progress.State = "failed";
                _progress.FinishedAt = _time.GetUtcNow();
                _progress.Error = ex.Message;
            }
        }
    }

    private async Task WalkAsync(string path, int depth, int maxDepth, User user, OpenListDbContext db, CancellationToken ct)
    {
        var resp = await _fs.ListAsync(new FsListReq { Path = path, PerPage = 1000 }, user, ct);
        if (resp.Code != 200 || resp.Data is null) return;

        lock (_lock) { _progress.Total += resp.Data.Total; }

        var normalizedParent = NormalizeParent(path);
        var batch = new List<SearchIndexEntry>();
        foreach (var entry in resp.Data.Content)
        {
            ct.ThrowIfCancellationRequested();
            batch.Add(new SearchIndexEntry
            {
                Parent = normalizedParent,
                Name = entry.Name,
                IsDir = entry.IsDir,
                Size = entry.Size,
                Path = JoinPath(normalizedParent, entry.Name),
                Type = entry.Type,
            });
            lock (_lock) { _progress.Indexed++; }
        }

        if (batch.Count > 0)
        {
            // INSERT in chunks; SQLite parameter limit is large but keep batches sane.
            foreach (var chunk in Chunk(batch, 200))
            {
                await db.SearchIndex.AddRangeAsync(chunk, ct);
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }
        }

        foreach (var entry in resp.Data.Content)
        {
            if (entry.IsDir && (maxDepth < 0 || depth < maxDepth))
            {
                var child = JoinPath(normalizedParent, entry.Name);
                await WalkAsync(child, depth + 1, maxDepth, user, db, ct);
            }
        }
    }

    private async Task<string> GetSettingAsync(string key, string fallback)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<ISettingService>();
            var r = await svc.GetAsync(key);
            return (r.Code == 200 && r.Data is not null && !string.IsNullOrWhiteSpace(r.Data.Value))
                ? r.Data.Value
                : fallback;
        }
        catch { return fallback; }
    }

    private static string NormalizeParent(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        if (!path.StartsWith('/')) path = "/" + path;
        return path.TrimEnd('/');
    }

    private static string JoinPath(string parent, string name)
        => parent == "/" ? "/" + name : parent + "/" + name;

    // Escape LIKE wildcards in user input so keywords containing % or _ match literally.
    private static string EscapeLike(string input)
        => (input ?? "").Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
    }
}
