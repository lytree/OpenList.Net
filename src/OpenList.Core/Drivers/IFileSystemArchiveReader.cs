using OpenList.Core.Models;
using Zio;

namespace OpenList.Core.Drivers;

/// <summary>
/// Extension interface for filesystems that can read archive metadata.
/// </summary>
public interface IFileSystemArchiveReader
{
    Task<ArchiveMeta> GetArchiveMetaAsync(UPath path, ArchiveArgs args, CancellationToken ct = default);
    Task<List<FileSystemItem>> ListArchiveAsync(UPath path, ArchiveInnerArgs args, CancellationToken ct = default);
    Task<Link?> ExtractAsync(UPath path, ArchiveInnerArgs args, CancellationToken ct = default);
}
