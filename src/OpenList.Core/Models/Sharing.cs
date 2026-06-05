namespace OpenList.Core.Models;

public class Sharing
{
    public string Id { get; set; } = string.Empty;
    public string FilesRaw { get; set; } = string.Empty;
    public DateTime? Expires { get; set; }
    public string? Pwd { get; set; }
    public int Accessed { get; set; }
    public int MaxAccessed { get; set; }
    public uint CreatorId { get; set; }
    public bool Disabled { get; set; }
    public string? Remark { get; set; }
    public string? Readme { get; set; }
    public string? Header { get; set; }

    // Sort
    public string? OrderBy { get; set; }
    public string? OrderDirection { get; set; }
    public string? ExtractFolder { get; set; }

    // Navigation
    public List<string> Files { get; set; } = [];
    public User? Creator { get; set; }

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
