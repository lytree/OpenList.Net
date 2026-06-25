using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// Search index service: build/update/clear search index, query files.
/// Mirrors OpenList Go <c>server/handles/search.go</c>.
/// </summary>
public interface ISearchService
{
    /// <summary>Full rebuild of the search index. paths defaults to ["/"], maxDepth -1 = unlimited.</summary>
    Task<Resp> BuildIndexAsync(IReadOnlyList<string> paths, int maxDepth, CancellationToken ct = default);
    /// <summary>Incremental update for the given paths.</summary>
    Task<Resp> UpdateIndexAsync(IReadOnlyList<string> paths, int maxDepth, CancellationToken ct = default);
    Task<Resp> StopIndexAsync(CancellationToken ct = default);
    Task<Resp> ClearIndexAsync(CancellationToken ct = default);
    Task<Resp<IndexProgress>> GetProgressAsync(CancellationToken ct = default);
    Task<Resp<SearchResp>> SearchAsync(SearchReq req, CancellationToken ct = default);
}

public sealed class IndexProgress
{
    public string State { get; set; } = "idle";
    public long Indexed { get; set; }
    public long Total { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>Timestamp of the last successful build/update completion (persisted).</summary>
    public DateTimeOffset? LastDoneTime { get; set; }
    /// <summary>Error message from the last run (empty if none).</summary>
    public string Error { get; set; } = string.Empty;
}
