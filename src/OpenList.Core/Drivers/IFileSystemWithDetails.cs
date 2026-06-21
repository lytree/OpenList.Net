using OpenList.Core.Models;

namespace OpenList.Core.Drivers;

/// <summary>
/// Extension interface for filesystems that provide storage capacity details.
/// </summary>
public interface IFileSystemWithDetails
{
    Task<StorageDetails?> GetDetailsAsync(CancellationToken ct = default);
}
