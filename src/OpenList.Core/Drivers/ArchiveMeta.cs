namespace OpenList.Core.Drivers;

/// <summary>Marker for the archive meta file (placeholder until full implementation).</summary>
public sealed class ArchiveMeta
{
    public string[] SupportedExtensions { get; set; } = [".zip", ".tar", ".gz", ".rar", ".7z"];
}
