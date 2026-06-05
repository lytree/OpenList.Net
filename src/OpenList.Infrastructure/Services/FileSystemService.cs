using OpenList.Core.Drivers;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using Microsoft.Extensions.Logging;

namespace OpenList.Infrastructure.Services;

public class FileSystemService : IFileSystemService
{
    private readonly IDriverFactory _driverFactory;
    private readonly ILogger<FileSystemService> _logger;

    public FileSystemService(IDriverFactory driverFactory, ILogger<FileSystemService> logger)
    {
        _driverFactory = driverFactory;
        _logger = logger;
    }

    public async Task<List<IObj>> ListAsync(string path, ListArgs? args = null, CancellationToken ct = default)
    {
        args ??= new ListArgs();
        var (driver, actualPath) = ResolveDriver(path);
        if (driver == null) return [];

        var dir = await GetObjAsync(driver, actualPath, ct);
        if (dir == null || !dir.IsDir()) return [];

        var objs = await driver.ListAsync(dir, args, ct);
        return objs;
    }

    public async Task<IObj?> GetAsync(string path, CancellationToken ct = default)
    {
        var (driver, actualPath) = ResolveDriver(path);
        if (driver == null) return null;
        return await GetObjAsync(driver, actualPath, ct);
    }

    public async Task<Link?> LinkAsync(string path, LinkArgs args, CancellationToken ct = default)
    {
        var (driver, actualPath) = ResolveDriver(path);
        if (driver == null) return null;

        var obj = await GetObjAsync(driver, actualPath, ct);
        if (obj == null || obj.IsDir()) return null;

        return await driver.LinkAsync(obj, args, ct);
    }

    public async Task MakeDirAsync(string path, CancellationToken ct = default)
    {
        var (driver, actualPath) = ResolveDriver(path);
        if (driver is IDriverMkdir mkdir)
        {
            var parentPath = GetParentPath(actualPath);
            var dirName = Path.GetFileName(actualPath.TrimEnd('/'));
            var parent = await GetObjAsync(driver, parentPath, ct);
            if (parent != null) await mkdir.MakeDirAsync(parent, dirName, ct);
        }
    }

    public async Task MoveAsync(string srcPath, string dstDir, CancellationToken ct = default)
    {
        var (srcDriver, srcActual) = ResolveDriver(srcPath);
        var (dstDriver, dstActual) = ResolveDriver(dstDir);
        if (srcDriver is IDriverMove move && srcDriver == dstDriver)
        {
            var srcObj = await GetObjAsync(srcDriver, srcActual, ct);
            var dstObj = await GetObjAsync(dstDriver, dstActual, ct);
            if (srcObj != null && dstObj != null) await move.MoveAsync(srcObj, dstObj, ct);
        }
    }

    public async Task CopyAsync(string srcPath, string dstDir, CancellationToken ct = default)
    {
        var (srcDriver, srcActual) = ResolveDriver(srcPath);
        var (dstDriver, dstActual) = ResolveDriver(dstDir);
        if (srcDriver is IDriverCopy copy && srcDriver == dstDriver)
        {
            var srcObj = await GetObjAsync(srcDriver, srcActual, ct);
            var dstObj = await GetObjAsync(dstDriver, dstActual, ct);
            if (srcObj != null && dstObj != null) await copy.CopyAsync(srcObj, dstObj, ct);
        }
    }

    public async Task RenameAsync(string path, string newName, CancellationToken ct = default)
    {
        var (driver, actualPath) = ResolveDriver(path);
        if (driver is IDriverRename rename)
        {
            var obj = await GetObjAsync(driver, actualPath, ct);
            if (obj != null) await rename.RenameAsync(obj, newName, ct);
        }
    }

    public async Task RemoveAsync(string path, CancellationToken ct = default)
    {
        var (driver, actualPath) = ResolveDriver(path);
        if (driver is IDriverRemove remove)
        {
            var obj = await GetObjAsync(driver, actualPath, ct);
            if (obj != null) await remove.RemoveAsync(obj, ct);
        }
    }

    public async Task PutAsync(string dstDir, FileStreamer file, Action<double> progress, CancellationToken ct = default)
    {
        var (driver, actualPath) = ResolveDriver(dstDir);
        if (driver is IDriverPut put)
        {
            var dir = await GetObjAsync(driver, actualPath, ct);
            if (dir != null) await put.PutAsync(dir, file, progress, ct);
        }
    }

    public async Task<object?> OtherAsync(OtherArgs args, CancellationToken ct = default)
    {
        if (args.Obj == null) return null;
        var storage = _driverFactory.FindStorageByPath(args.Obj.GetPath());
        if (storage == null) return null;
        var driver = _driverFactory.GetDriver(storage.MountPath);
        if (driver is IDriverOther other)
            return await other.OtherAsync(args, ct);
        return null;
    }

    public async Task<Storage?> GetStorageAsync(string path, CancellationToken ct = default)
    {
        return await Task.FromResult(_driverFactory.FindStorageByPath(path));
    }

    private (IDriver? driver, string actualPath) ResolveDriver(string path)
    {
        var storage = _driverFactory.FindStorageByPath(path);
        if (storage == null) return (null, path);
        var driver = _driverFactory.GetDriver(storage.MountPath);
        var actualPath = path[storage.MountPath.TrimEnd('/').Length..].TrimStart('/');
        if (string.IsNullOrEmpty(actualPath)) actualPath = "/";
        return (driver, actualPath);
    }

    private static async Task<IObj?> GetObjAsync(IDriver driver, string path, CancellationToken ct)
    {
        if (driver is IDriverGetter getter)
            return await getter.GetAsync(path, ct);
        if (driver is IDriverGetRoot rootGetter && path == "/")
            return await rootGetter.GetRootAsync(ct);
        return null;
    }

    private static string GetParentPath(string path)
    {
        var trimmed = path.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : trimmed[..lastSlash];
    }
}
