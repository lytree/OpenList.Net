using OpenList.Core.Models;
using Zio;

namespace OpenList.Core.Drivers;

/// <summary>
/// Extension interface for filesystems that can decompress archives.
/// </summary>
public interface IFileSystemArchiveDecompress
{
    Task ArchiveDecompressAsync(UPath srcPath, UPath dstDir, ArchiveDecompressArgs args, CancellationToken ct = default);
}
