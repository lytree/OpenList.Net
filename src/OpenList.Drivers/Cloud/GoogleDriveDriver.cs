using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;

namespace OpenList.Drivers.GoogleDrive;

public class GoogleDriveDriver : ZioDriverBase
{
    public override DriverConfig Config { get; } = new() { Name = "GoogleDrive" };
    public override object Addition => new();
    public override DriverInfo GetDriverInfo() => new() { Common = [], Additional = [], Config = Config };
    public override Task InitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;
}
