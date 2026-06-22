using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// Search index service: build/update/clear search index, query files.
/// Mirrors OpenList Go <c>server/handles/search.go</c>.
/// </summary>
public interface ISearchService
{
    Task<Resp> BuildIndexAsync(CancellationToken ct = default);
    Task<Resp> UpdateIndexAsync(string path, CancellationToken ct = default);
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
}
