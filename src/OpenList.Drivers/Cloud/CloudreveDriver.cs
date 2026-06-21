using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;

namespace OpenList.Drivers.Cloudreve;

public class CloudreveDriver : ZioDriverBase
{
    public override DriverConfig Config { get; } = new() { Name = "Cloudreve" };
    public override object Addition => new();
    public override DriverInfo GetDriverInfo() => new() { Common = [], Additional = [], Config = Config };
    public override Task InitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public class CloudreveV4Driver : ZioDriverBase
{
    public override DriverConfig Config { get; } = new() { Name = "CloudreveV4" };
    public override object Addition => new();
    public override DriverInfo GetDriverInfo() => new() { Common = [], Additional = [], Config = Config };
    public override Task InitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;
}
