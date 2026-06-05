using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;

namespace OpenList.Drivers.OneDrive;

public class OneDriveDriver : DriverBase, IDriverGetter
{
    public override DriverConfig Config() => new() { Name = "OneDrive" };
    public override object GetAddition() => new();
    public override Task InitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default) => Task.FromResult(new List<IObj>());
    public override Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default) => Task.FromResult<Link?>(null);
    public Task<IObj?> GetAsync(string path, CancellationToken ct = default) => Task.FromResult<IObj?>(null);
}

public class OneDriveAppDriver : DriverBase, IDriverGetter
{
    public override DriverConfig Config() => new() { Name = "OneDriveApp" };
    public override object GetAddition() => new();
    public override Task InitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default) => Task.FromResult(new List<IObj>());
    public override Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default) => Task.FromResult<Link?>(null);
    public Task<IObj?> GetAsync(string path, CancellationToken ct = default) => Task.FromResult<IObj?>(null);
}

public class OneDriveShareLinkDriver : DriverBase, IDriverGetter
{
    public override DriverConfig Config() => new() { Name = "OneDriveShareLink" };
    public override object GetAddition() => new();
    public override Task InitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default) => Task.FromResult(new List<IObj>());
    public override Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default) => Task.FromResult<Link?>(null);
    public Task<IObj?> GetAsync(string path, CancellationToken ct = default) => Task.FromResult<IObj?>(null);
}
