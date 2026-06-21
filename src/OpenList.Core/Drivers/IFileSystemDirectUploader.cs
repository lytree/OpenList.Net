using OpenList.Core.Models;

namespace OpenList.Core.Drivers;

/// <summary>
/// Extension interface for filesystems that support direct upload (bypassing server).
/// </summary>
public interface IFileSystemDirectUploader
{
    List<string> GetDirectUploadTools();
    Task<object?> GetDirectUploadInfoAsync(string tool, IObj dstDir, string fileName, long fileSize, CancellationToken ct = default);
}
