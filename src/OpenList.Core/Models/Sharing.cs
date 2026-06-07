namespace OpenList.Core.Models;

using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

public class Sharing
{
    public string Id { get; set; } = string.Empty;
    [JsonIgnore] public string FilesRaw { get; set; } = string.Empty;
    public DateTime? Expires { get; set; }
    public string? Pwd { get; set; }
    public int Accessed { get; set; }
    [JsonPropertyName("max_accessed")] public int MaxAccessed { get; set; }
    [JsonIgnore] public uint CreatorId { get; set; }
    public bool Disabled { get; set; }
    public string? Remark { get; set; }
    public string? Readme { get; set; }
    public string? Header { get; set; }

    // Sort
    [JsonPropertyName("order_by")] public string? OrderBy { get; set; }
    [JsonPropertyName("order_direction")] public string? OrderDirection { get; set; }
    [JsonPropertyName("extract_folder")] public string? ExtractFolder { get; set; }

    // Navigation
    public List<string> Files { get; set; } = [];
    [JsonIgnore] public User? Creator { get; set; }

    // Computed (not stored in DB)
    [NotMapped]
    [JsonPropertyName("creator")]
    public string? CreatorName { get; set; }
    [NotMapped]
    [JsonPropertyName("creator_role")]
    public int CreatorRole { get; set; }

    public bool Valid()
    {
        if (Disabled) return false;
        if (MaxAccessed > 0 && Accessed >= MaxAccessed) return false;
        if (Files.Count == 0) return false;
        if (Creator == null || !Creator.HasPermission(UserPermission.CanShare)) return false;
        if (Expires.HasValue && Expires.Value < DateTime.UtcNow) return false;
        return true;
    }

    public bool Verify(string? pwd) => string.IsNullOrEmpty(Pwd) || Pwd == pwd;
}
