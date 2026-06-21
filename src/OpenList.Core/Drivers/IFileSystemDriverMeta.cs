using OpenList.Core.Models;

namespace OpenList.Core.Drivers;

/// <summary>
/// Extension interface providing driver metadata (configuration, storage binding, lifecycle).
/// All Zio-based drivers must implement this to integrate with the mount service.
/// </summary>
public interface IFileSystemDriverMeta
{
    DriverConfig Config { get; }
    Storage Storage { get; set; }
    object Addition { get; }
    DriverInfo GetDriverInfo();
    Task InitAsync(CancellationToken ct = default);
    Task DropAsync(CancellationToken ct = default);
}
