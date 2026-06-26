using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace OpenList.Core.Models;

/// <summary>
/// A single persisted search-index row. Mirrors OpenList Go's database search
/// index (internal/search). Rows are produced by <c>SearchService</c> during a
/// build/update walk and queried by <c>SearchAsync</c> when the
/// <c>search_index</c> setting is <c>"database"</c>.
/// </summary>
public sealed class SearchIndexEntry
{
    public long Id { get; set; }
    /// <summary>Parent directory (normalized absolute path, e.g. "/docs").</summary>
    public string Parent { get; set; } = string.Empty;
    /// <summary>Entry name (file or directory).</summary>
    public string Name { get; set; } = string.Empty;
    [JsonPropertyName("is_dir")]
    public bool IsDir { get; set; }
    public long Size { get; set; }
    /// <summary>Full path = Parent + "/" + Name. Indexed for exact lookups.</summary>
    public string Path { get; set; } = string.Empty;
    /// <summary>File type code (UNKNOWN=0, FOLDER=1, VIDEO=2, AUDIO=3, TEXT=4, IMAGE=5).</summary>
    public int Type { get; set; }
}
