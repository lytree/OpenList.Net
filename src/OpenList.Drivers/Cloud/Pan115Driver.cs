using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;

namespace OpenList.Drivers.Pan115;

public class Pan115Driver : DriverBase, IDriverGetter
{
    public override DriverConfig Config() => new() { Name = "115" };
    public override object GetAddition() => new();
    public override Task InitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default) => Task.FromResult(new List<IObj>());
    public override Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default) => Task.FromResult<Link?>(null);
    public Task<IObj?> GetAsync(string path, CancellationToken ct = default) => Task.FromResult<IObj?>(null);
}

public class Pan115OpenDriver : DriverBase, IDriverGetter
{
    public override DriverConfig Config() => new() { Name = "115Open" };
    public override object GetAddition() => new();
    public override Task InitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default) => Task.FromResult(new List<IObj>());
    public override Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default) => Task.FromResult<Link?>(null);
    public Task<IObj?> GetAsync(string path, CancellationToken ct = default) => Task.FromResult<IObj?>(null);
}

public class Pan115ShareDriver : DriverBase, IDriverGetter
{
    public override DriverConfig Config() => new() { Name = "115Share" };
    public override object GetAddition() => new();
    public override Task InitAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;
    public override Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default) => Task.FromResult(new List<IObj>());
    public override Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default) => Task.FromResult<Link?>(null);
    public Task<IObj?> GetAsync(string path, CancellationToken ct = default) => Task.FromResult<IObj?>(null);
}
