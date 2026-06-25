using System.Diagnostics;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// Search index service. Tracks build/update progress in-memory and walks the
/// filesystem via <see cref="IFileSystemService"/>. The actual indexed entries
/// are not persisted yet (Phase: progress + control surface for the frontend);
/// <see cref="SearchAsync"/> delegates to <see cref="IFileSystemService.SearchAsync"/>.
/// Mirrors OpenList Go <c>server/handles/search.go</c>.
/// </summary>
public sealed class SearchService : ISearchService
{
    private readonly IFileSystemService _fs;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private IndexProgress _progress = new();
    private CancellationTokenSource? _cts;
    // Persisted across runs in-memory (not DB-backed yet).
    private DateTimeOffset? _lastDoneTime;

    public SearchService(IFileSystemService fs, TimeProvider? time = null)
    {
        _fs = fs;
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
        _ = Task.Run(() => IndexAsync(effectivePaths, maxDepth, _cts.Token), CancellationToken.None);

        return Task.FromResult(Resp.Ok());
    }

    public Task<Resp> StopIndexAsync(CancellationToken ct = default)
    {
        lock (_lock) { _cts?.Cancel(); }
        return Task.FromResult(Resp.Ok());
    }

    public Task<Resp> ClearIndexAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _progress = new IndexProgress { State = "idle", LastDoneTime = _lastDoneTime };
        }
        // No persistent index store yet; clearing is a no-op beyond resetting progress.
        return Task.FromResult(Resp.Ok());
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

    public Task<Resp<SearchResp>> SearchAsync(SearchReq req, CancellationToken ct = default)
        => _fs.SearchAsync(req, new User { Id = 0, Username = "search", Role = UserRole.Admin }, ct);

    private async Task IndexAsync(IReadOnlyList<string> paths, int maxDepth, CancellationToken ct)
    {
        try
        {
            var admin = new User { Id = 0, Username = "indexer", Role = UserRole.Admin };
            foreach (var root in paths)
            {
                ct.ThrowIfCancellationRequested();
                await WalkAsync(root, 0, maxDepth, admin, ct);
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

    private async Task WalkAsync(string path, int depth, int maxDepth, User user, CancellationToken ct)
    {
        var resp = await _fs.ListAsync(new FsListReq { Path = path, PerPage = 1000 }, user, ct);
        if (resp.Code != 200 || resp.Data is null) return;

        lock (_lock) { _progress.Total += resp.Data.Total; }

        foreach (var entry in resp.Data.Content)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock) { _progress.Indexed++; }

            if (entry.IsDir && (maxDepth < 0 || depth < maxDepth))
            {
                var child = string.IsNullOrEmpty(path) || path == "/"
                    ? "/" + entry.Name
                    : path.TrimEnd('/') + "/" + entry.Name;
                await WalkAsync(child, depth + 1, maxDepth, user, ct);
            }
        }
    }
}
