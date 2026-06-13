using OpenList.Core.Models;
using System.Text.Json.Serialization;

namespace OpenList.Core.Interfaces;

public interface ISearchService
{
    Task BuildIndexAsync(CancellationToken ct = default);
    Task UpdateIndexAsync(string parent, List<IObj> objs, CancellationToken ct = default);
    Task<List<SearchResult>> SearchAsync(string parent, string keywords, int page, int perPage, CancellationToken ct = default);
    Task StopIndexAsync(CancellationToken ct = default);
    Task ClearIndexAsync(CancellationToken ct = default);
    Task<IndexProgress> GetProgressAsync(CancellationToken ct = default);
}

public class SearchResult
{
    public string Parent { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    [JsonPropertyName("is_dir")] public bool IsDir { get; set; }
    public long Size { get; set; }
    public DateTime Modified { get; set; }
    public int Type { get; set; }
}

public class IndexProgress
{
    public string State { get; set; } = "";
    public double Progress { get; set; }
    public string? LastError { get; set; }
}
