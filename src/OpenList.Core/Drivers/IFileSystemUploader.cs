using Zio;

namespace OpenList.Core.Drivers;

/// <summary>
/// Extension interface for filesystems that support upload with progress reporting.
/// </summary>
public interface IFileSystemUploader
{
    Task<UPath> UploadAsync(UPath dstDir, Stream stream, string fileName, Action<double> progress, CancellationToken ct = default);
}
