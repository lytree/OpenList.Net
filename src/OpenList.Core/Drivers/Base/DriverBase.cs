using OpenList.Core.Drivers;
using OpenList.Core.Models;

namespace OpenList.Core.Drivers.Base;

/// <summary>
/// Base driver implementation with common functionality
/// </summary>
public abstract class DriverBase : IDriver
{
    protected Storage _storage = null!;
    private DriverConfig? _config;

    public abstract DriverConfig Config();
    public abstract object GetAddition();
    public abstract Task InitAsync(CancellationToken ct = default);
    public abstract Task DropAsync(CancellationToken ct = default);
    public abstract Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default);
    public abstract Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default);

    public virtual Storage GetStorage() => _storage;
    public virtual void SetStorage(Storage storage) => _storage = storage;
}

/// <summary>
/// Base driver with root path support
/// </summary>
public abstract class RootPathDriver : DriverBase
{
    public string RootFolderPath { get; set; } = "";

    public string GetRootPath() => RootFolderPath;
    public void SetRootPath(string path) => RootFolderPath = path;
}

/// <summary>
/// Base driver with root ID support
/// </summary>
public abstract class RootIdDriver : DriverBase
{
    public string RootFolderID { get; set; } = "";

    public string GetRootId() => RootFolderID;
}
