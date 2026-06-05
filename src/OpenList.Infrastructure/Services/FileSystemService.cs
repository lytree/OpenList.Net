using OpenList.Core.Drivers;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace OpenList.Infrastructure.Services;

public class FileSystemService : IFileSystemService
{
    private readonly IDriverFactory _driverFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<FileSystemService> _logger;

    public FileSystemService(IDriverFactory driverFactory, IMemoryCache cache, ILogger<FileSystemService> logger)
    {
        _driverFactory = driverFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<List<IObj>> ListAsync(string path, ListArgs? args = null, CancellationToken ct = default)
    {
        args ??= new ListArgs();
        var (driver, actualPath) = ResolveDriver(path);
        if (driver == null) return [];

        var dir = await GetObjAsync(driver, actualPath, ct);
        if (dir == null || !dir.IsDir()) return [];

        // Check cache (skip if refresh requested or driver has NoCache)
        var cacheKey = $"dir:{driver.GetStorage().MountPath}:{actualPath}";
        var config = driver.Config();
        if (!args.Refresh && config.NoCache == false)
        {
            if (_cache.TryGetValue(cacheKey, out List<IObj>? cached) && cached != null)
                return cached;
        }

        var objs = await driver.ListAsync(dir, args, ct);

        // Sort (Go: LocalSort → SortFiles + ExtractFolder)
        var storage = driver.GetStorage();
        if (config.LocalSort)
            SortFiles(objs, storage.OrderBy, storage.OrderDirection);
        ExtractFolder(objs, storage.ExtractFolder);

        // Cache result with TTL from storage config
        if (config.NoCache == false)
        {
            var ttl = TimeSpan.FromMinutes(Math.Max(driver.GetStorage().CacheExpiration, 1));
            _cache.Set(cacheKey, objs, ttl);
        }

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

        // Check link cache
        var cacheKey = $"link:{driver.GetStorage().MountPath}:{actualPath}";
        if (driver.Config().NoCache == false && _cache.TryGetValue(cacheKey, out Link? cachedLink))
            return cachedLink;

        var link = await driver.LinkAsync(obj, args, ct);

        // Cache link for 30 minutes
        if (link != null && driver.Config().NoCache == false)
            _cache.Set(cacheKey, link, TimeSpan.FromMinutes(30));

        return link;
    }

    public async Task MakeDirAsync(string path, CancellationToken ct = default)
    {
        var (driver, actualPath) = ResolveDriver(path);
        if (driver is IDriverMkdir mkdir)
        {
            var parentPath = GetParentPath(actualPath);
            var dirName = Path.GetFileName(actualPath.TrimEnd('/'));
            var parent = await GetObjAsync(driver, parentPath, ct);
            if (parent != null)
            {
                await mkdir.MakeDirAsync(parent, dirName, ct);
                InvalidateDirectoryCache(driver, parentPath);
            }
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
            if (srcObj != null && dstObj != null)
            {
                await move.MoveAsync(srcObj, dstObj, ct);
                InvalidateDirectoryCache(srcDriver, GetParentPath(srcActual));
                InvalidateDirectoryCache(dstDriver, dstActual);
                InvalidateLinkCache(srcDriver, srcActual);
            }
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
            if (srcObj != null && dstObj != null)
            {
                await copy.CopyAsync(srcObj, dstObj, ct);
                InvalidateDirectoryCache(dstDriver, dstActual);
            }
        }
    }

    public async Task RenameAsync(string path, string newName, CancellationToken ct = default)
    {
        var (driver, actualPath) = ResolveDriver(path);
        if (driver is IDriverRename rename)
        {
            var obj = await GetObjAsync(driver, actualPath, ct);
            if (obj != null)
            {
                await rename.RenameAsync(obj, newName, ct);
                InvalidateDirectoryCache(driver, GetParentPath(actualPath));
                InvalidateLinkCache(driver, actualPath);
            }
        }
    }

    public async Task RemoveAsync(string path, CancellationToken ct = default)
    {
        var (driver, actualPath) = ResolveDriver(path);
        if (driver is IDriverRemove remove)
        {
            var obj = await GetObjAsync(driver, actualPath, ct);
            if (obj != null)
            {
                await remove.RemoveAsync(obj, ct);
                InvalidateDirectoryCache(driver, GetParentPath(actualPath));
                InvalidateLinkCache(driver, actualPath);
            }
        }
    }

    public async Task PutAsync(string dstDir, FileStreamer file, Action<double> progress, CancellationToken ct = default)
    {
        var (driver, actualPath) = ResolveDriver(dstDir);
        if (driver is IDriverPut put)
        {
            var dir = await GetObjAsync(driver, actualPath, ct);
            if (dir != null)
            {
                await put.PutAsync(dir, file, progress, ct);
                InvalidateDirectoryCache(driver, actualPath);
            }
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

    public async Task<StorageDetails?> GetStorageDetailsAsync(string path, CancellationToken ct = default)
    {
        var (driver, _) = ResolveDriver(path);
        if (driver is not IDriverWithDetails detailDriver) return null;

        // Check cache (Go: detailCache with 30min TTL)
        var cacheKey = $"detail:{driver.GetStorage().MountPath}";
        if (_cache.TryGetValue(cacheKey, out StorageDetails? cached))
            return cached;

        var details = await detailDriver.GetDetailsAsync(ct);
        if (details != null)
            _cache.Set(cacheKey, details, TimeSpan.FromMinutes(30));

        return details;
    }

    private void InvalidateDirectoryCache(IDriver driver, string dirPath)
    {
        var cacheKey = $"dir:{driver.GetStorage().MountPath}:{dirPath}";
        _cache.Remove(cacheKey);
    }

    private void InvalidateLinkCache(IDriver driver, string filePath)
    {
        var cacheKey = $"link:{driver.GetStorage().MountPath}:{filePath}";
        _cache.Remove(cacheKey);
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

    /// <summary>Sort files by OrderBy/OrderDirection (Go: model.SortFiles)</summary>
    private static void SortFiles(List<IObj> objs, string? orderBy, string? orderDirection)
    {
        if (string.IsNullOrEmpty(orderBy)) return;
        var desc = orderDirection == "desc";

        objs.Sort((a, b) =>
        {
            int cmp = orderBy switch
            {
                "name" => string.Compare(a.GetName(), b.GetName(), StringComparison.Ordinal),
                "size" => a.GetSize().CompareTo(b.GetSize()),
                "modified" => a.GetModTime().CompareTo(b.GetModTime()),
                _ => 0
            };
            return desc ? -cmp : cmp;
        });
    }

    /// <summary>Extract folders to front or back (Go: model.ExtractFolder)</summary>
    private static void ExtractFolder(List<IObj> objs, string? extractFolder)
    {
        if (string.IsNullOrEmpty(extractFolder)) return;
        var front = extractFolder == "front";
        objs.Sort((a, b) =>
        {
            if (a.IsDir() && !b.IsDir()) return front ? -1 : 1;
            if (!a.IsDir() && b.IsDir()) return front ? 1 : -1;
            return 0;
        });
    }
}
