using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;

namespace OpenList.Drivers.OneDrive;

public class OneDriveDriver : ZioDriverBase
{
    public override DriverConfig Config { get; } = new() { Name = "OneDrive" };
    public override object Addition => new();
    public override DriverInfo GetDriverInfo() => new() { Common = [], Additional = [], Config = Config };
    public override Task InitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public class OneDriveAppDriver : ZioDriverBase
{
    public override DriverConfig Config { get; } = new() { Name = "OneDriveApp" };
    public override object Addition => new();
    public override DriverInfo GetDriverInfo() => new() { Common = [], Additional = [], Config = Config };
    public override Task InitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public class OneDriveShareLinkDriver : ZioDriverBase
{
    public override DriverConfig Config { get; } = new() { Name = "OneDriveShareLink" };
    public override object Addition => new();
    public override DriverInfo GetDriverInfo() => new() { Common = [], Additional = [], Config = Config };
    public override Task InitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;
}
