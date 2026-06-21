using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;

namespace OpenList.Drivers.Pan115;

public class Pan115Driver : ZioDriverBase
{
    public override DriverConfig Config { get; } = new() { Name = "115" };
    public override object Addition => new();
    public override DriverInfo GetDriverInfo() => new() { Common = [], Additional = [], Config = Config };
    public override Task InitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public class Pan115OpenDriver : ZioDriverBase
{
    public override DriverConfig Config { get; } = new() { Name = "115Open" };
    public override object Addition => new();
    public override DriverInfo GetDriverInfo() => new() { Common = [], Additional = [], Config = Config };
    public override Task InitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public class Pan115ShareDriver : ZioDriverBase
{
    public override DriverConfig Config { get; } = new() { Name = "115Share" };
    public override object Addition => new();
    public override DriverInfo GetDriverInfo() => new() { Common = [], Additional = [], Config = Config };
    public override Task InitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;
}
