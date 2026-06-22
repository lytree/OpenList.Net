namespace OpenList.Core.Drivers;

/// <summary>Metadata describing a driver's capabilities (read-only, cloud, archive support, etc.).</summary>
public interface IFileSystemDriverMeta
{
    bool SupportsLink { get; }
    bool SupportsUpload { get; }
    bool SupportsArchiveRead { get; }
    bool SupportsArchiveDecompress { get; }
    bool SupportsDirectUpload { get; }
}
