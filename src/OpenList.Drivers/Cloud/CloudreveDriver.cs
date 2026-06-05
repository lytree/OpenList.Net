using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;

namespace OpenList.Drivers.Cloudreve;

public class CloudreveDriver : DriverBase, IDriverGetter
{
    public override DriverConfig Config() => new() { Name = "Cloudreve" };
    public override object GetAddition() => new();
    public override Task InitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default) => Task.FromResult(new List<IObj>());
    public override Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default) => Task.FromResult<Link?>(null);
    public Task<IObj?> GetAsync(string path, CancellationToken ct = default) => Task.FromResult<IObj?>(null);
}

public class CloudreveV4Driver : DriverBase, IDriverGetter
{
    public override DriverConfig Config() => new() { Name = "CloudreveV4" };
    public override object GetAddition() => new();
    public override Task InitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default) => Task.FromResult(new List<IObj>());
    public override Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default) => Task.FromResult<Link?>(null);
    public Task<IObj?> GetAsync(string path, CancellationToken ct = default) => Task.FromResult<IObj?>(null);
}
