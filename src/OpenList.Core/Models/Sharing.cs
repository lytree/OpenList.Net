using System.Text.Json.Serialization;

namespace OpenList.Core.Models;

/// <summary>
/// Mirrors Go model.Sharing. Uses long Id (auto-increment) — frontend's
/// string <c>id</c> field is the user-visible share code stored in <see cref="ShareCode"/>.
/// </summary>
public sealed class Sharing
{
    public long Id { get; set; }

    /// <summary>User-visible share id (Go: string id). Empty → server generates.</summary>
    [JsonPropertyName("id")]
    public string ShareCode { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    /// <summary>Multiple shared paths joined (Go: Files []string). Stored as JSON.</summary>
    public string FilesJson { get; set; } = "[]";

    [JsonPropertyName("pwd")]
    public string? Password { get; set; }

    [JsonPropertyName("expires")]
    public DateTimeOffset? Expiration { get; set; }

    [JsonPropertyName("max_accessed")]
    public int MaxAccessed { get; set; }

    [JsonPropertyName("accessed")]
    public int Accessed { get; set; }

    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; }

    [JsonPropertyName("remark")]
    public string? Remark { get; set; }

    [JsonPropertyName("readme")]
    public string? Readme { get; set; }

    [JsonPropertyName("header")]
    public string? Header { get; set; }

    [JsonPropertyName("order_by")]
    public string? OrderBy { get; set; }

    [JsonPropertyName("order_direction")]
    public string? OrderDirection { get; set; }

    [JsonPropertyName("extract_folder")]
    public string? ExtractFolder { get; set; }

    public long OwnerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonIgnore]
    public bool Enabled { get => !Disabled; set => Disabled = !value; }
}
