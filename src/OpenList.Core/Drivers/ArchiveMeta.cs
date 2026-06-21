namespace OpenList.Core.Drivers;

/// <summary>
/// Metadata for an archive file.
/// </summary>
public class ArchiveMeta
{
    public bool IsEncrypted { get; set; }
    public List<ArchiveEntry> Entries { get; set; } = [];
}

/// <summary>
/// Represents a single entry in an archive.
/// </summary>
public class ArchiveEntry
{
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public bool IsDir { get; set; }
    public DateTime Modified { get; set; }
    public string? InnerPath { get; set; }
}
