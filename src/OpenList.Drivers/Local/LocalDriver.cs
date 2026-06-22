using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using Zio;
using Zio.FileSystems;

namespace OpenList.Drivers.Local;

/// <summary>
/// Local filesystem driver. Mounts a directory on the server's filesystem.
/// Placeholder skeleton — full implementation pending Phase 2.
/// </summary>
public sealed class LocalDriver : ZioDriverBase
{
    public override string DriverName => "Local";
    public override bool ReadOnly => false;
    public override bool IsCloudDriver => false;

    public override Task InitAsync(DriverConfig config, CancellationToken ct = default)
    {
        // TODO: parse addition (root path), mount via MountFileSystem
        return Task.CompletedTask;
    }
}
