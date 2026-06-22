namespace OpenList.Core.Drivers;

/// <summary>Driver exposes a direct download/proxy URL for files (cloud drivers).</summary>
public interface IFileSystemLinkProvider
{
    /// <summary>Return a direct link URL for the given path, or null if not supported.</summary>
    Task<Uri?> GetLinkAsync(string path, CancellationToken ct = default);
}

/// <summary>Driver supports uploading files via stream or form.</summary>
public interface IFileSystemUploader
{
    Task PutAsync(string path, Stream stream, CancellationToken ct = default);
    Task PutFormAsync(string path, Stream stream, string fileName, CancellationToken ct = default);
}

/// <summary>Driver exposes additional file details beyond <see cref="Models.Obj"/>.</summary>
public interface IFileSystemWithDetails
{
    Task<Dictionary<string, object?>> GetDetailsAsync(string path, CancellationToken ct = default);
}

/// <summary>Driver can read archive contents (list entries).</summary>
public interface IFileSystemArchiveReader
{
    Task<IReadOnlyList<ArchiveEntry>> ListArchiveEntriesAsync(string path, CancellationToken ct = default);
}

/// <summary>Driver can decompress archives in-place.</summary>
public interface IFileSystemArchiveDecompress
{
    Task DecompressAsync(string archivePath, string destPath, string? password = null, CancellationToken ct = default);
}

/// <summary>Driver supports direct upload via pre-signed URL or chunked upload API.</summary>
public interface IFileSystemDirectUploader
{
    Task<DirectUploadInfo> GetDirectUploadInfoAsync(string path, long size, CancellationToken ct = default);
}

public sealed class ArchiveEntry
{
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public bool IsDir { get; set; }
    public DateTimeOffset Modified { get; set; }
}

public sealed class DirectUploadInfo
{
    public string Url { get; set; } = string.Empty;
    public Dictionary<string, string> Headers { get; set; } = new();
    public string? Method { get; set; }
}
