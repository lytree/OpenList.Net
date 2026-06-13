using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OpenList.Infrastructure.Services;

public class FileSystemService : IFileSystemService
{
    private readonly IDriverFactory _driverFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<FileSystemService> _logger;
    private readonly IServiceProvider _serviceProvider;

    public FileSystemService(IDriverFactory driverFactory, IMemoryCache cache, ILogger<FileSystemService> logger, IServiceProvider serviceProvider)
    {
        _driverFactory = driverFactory;
        _cache = cache;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public async Task<List<IObj>> ListAsync(string path, ListArgs? args = null, CancellationToken ct = default)
    {
        args ??= new ListArgs();

        // Go: virtualFiles are always computed for any path, then merged with actual driver results
        var virtualFiles = _driverFactory.GetVirtualFilesByPath(path);

        var (driver, actualPath) = ResolveDriver(path);
        List<IObj> driverObjs = [];

        if (driver != null)
        {
            var dir = await GetObjAsync(driver, actualPath, ct);
            if (dir != null && dir.IsDir())
            {
                // Check cache (skip if refresh requested or driver has NoCache)
                var cacheKey = $"dir:{driver.GetStorage().MountPath}:{actualPath}";
                var config = driver.Config();
                if (!args.Refresh && config.NoCache == false)
                {
                    if (_cache.TryGetValue(cacheKey, out List<IObj>? cached) && cached != null)
                        driverObjs = cached;
                    else
                    {
                        driverObjs = await driver.ListAsync(dir, args, ct);

                        // Sort (Go: LocalSort → SortFiles + ExtractFolder)
                        var storage = driver.GetStorage();
                        if (config.LocalSort)
                            SortFiles(driverObjs, storage.OrderBy, storage.OrderDirection);
                        ExtractFolder(driverObjs, storage.ExtractFolder);

                        // Cache result with TTL from storage config
                        if (config.NoCache == false)
                        {
                            var ttl = TimeSpan.FromMinutes(Math.Max(driver.GetStorage().CacheExpiration, 1));
                            _cache.Set(cacheKey, driverObjs, ttl);
                        }
                    }
                }
                else
                {
                    driverObjs = await driver.ListAsync(dir, args, ct);

                    var storage = driver.GetStorage();
                    if (config.LocalSort)
                        SortFiles(driverObjs, storage.OrderBy, storage.OrderDirection);
                    ExtractFolder(driverObjs, storage.ExtractFolder);

                    if (config.NoCache == false)
                    {
                        var ttl = TimeSpan.FromMinutes(Math.Max(driver.GetStorage().CacheExpiration, 1));
                        _cache.Set(cacheKey, driverObjs, ttl);
                    }
                }
            }
        }

        // Merge: virtual files override driver objects with the same name (Go: om.Merge)
        // Virtual files (mount points) take priority over real files with the same name
        if (virtualFiles.Count == 0) return driverObjs;
        if (driverObjs.Count == 0) return virtualFiles.Cast<IObj>().ToList();

        var virtualNames = new HashSet<string>(virtualFiles.Select(v => v.GetName()));
        var merged = new List<IObj>();
        merged.AddRange(virtualFiles);
        foreach (var obj in driverObjs)
        {
            if (!virtualNames.Contains(obj.GetName()))
                merged.Add(obj);
        }
        return merged;
    }

    public async Task<IObj?> GetAsync(string path, CancellationToken ct = default)
    {
        // Go: first check if it's a virtual file (mount point)
        if (path != "/")
        {
            var parentDir = GetParentPath(path);
            var name = path.TrimEnd('/').Split('/').Last();
            var virtualFiles = _driverFactory.GetVirtualFilesByPath(parentDir);
            var virtualMatch = virtualFiles.FirstOrDefault(v => v.GetName() == name);
            if (virtualMatch != null) return virtualMatch;
        }

        var (driver, actualPath) = ResolveDriver(path);
        if (driver == null)
        {
            // Root path: return virtual root object (Go: model.Object{Name: "root", IsFolder: true})
            if (path == "/")
                return new ObjItem { Name = "root", Path = "/", IsFolder = true, Mask = ObjMask.ReadOnly | ObjMask.Virtual };
            return null;
        }
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

    public async Task MergeAsync(string srcPath, string dstDir, CancellationToken ct = default)
    {
        // Go: fs.Merge uses transfer(merge, ...) which recursively merges directory contents
        // At the driver level, merge uses the same IDriverCopy interface
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

    public async Task<TaskInfo?> PutAsTaskAsync(string dstDir, FileStreamer file, CancellationToken ct = default)
    {
        var taskService = _serviceProvider.GetService<ITaskService>();
        if (taskService == null) return null;
        var task = await taskService.CreateTaskAsync("upload", $"Upload {dstDir}/{file.Obj.GetName()}", async (taskId, innerCt) =>
        {
            await PutAsync(dstDir, file, _ => { }, innerCt);
        }, ct);
        return task;
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

    private async Task<IObj?> GetObjAsync(IDriver driver, string path, CancellationToken ct)
    {
        // Root path: handle like Go's op.Get (GetRooter → IRootPath/IRootId → Getter → fallback)
        if (path == "/")
        {
            // 1. Try IDriverGetRoot (Go: GetRooter)
            if (driver is IDriverGetRoot rootGetter)
            {
                try { return await rootGetter.GetRootAsync(ct); }
                catch { /* fall through */ }
            }

            // 2. Try RootPathDriver / RootIdDriver (Go: IRootPath / IRootId)
            // Go: Object{Path: r.GetRootPath(), Name: "root"} — Path is actual filesystem path
            // so that driver.List can use dir.GetPath() directly to read the directory
            if (driver is RootPathDriver rpd)
            {
                return new ObjItem
                {
                    Name = "root",
                    Path = rpd.GetRootPath(),
                    IsFolder = true,
                    Modified = driver.GetStorage().Modified,
                    Mask = ObjMask.Locked
                };
            }
            if (driver is RootIdDriver rid)
            {
                return new ObjItem
                {
                    Name = "root",
                    ID = rid.GetRootId(),
                    Path = "/",
                    IsFolder = true,
                    Modified = driver.GetStorage().Modified,
                    Mask = ObjMask.Locked
                };
            }

            // 3. Try IDriverGetter (Go: Getter)
            if (driver is IDriverGetter getter)
            {
                try
                {
                    var obj = await getter.GetAsync(path, ct);
                    if (obj != null) return obj;
                }
                catch { /* fall through */ }
            }

            return null;
        }

        // Non-root path: try Getter first, then fallback to listing parent directory
        // (mirrors Go's op.Get logic)
        if (driver is IDriverGetter getter2)
        {
            try
            {
                var obj = await getter2.GetAsync(path, ct);
                if (obj != null) return obj;
            }
            catch { /* fall through to parent dir listing */ }
        }

        // Fallback: list parent directory and find by name (Go: op.Get fallback)
        var dir = GetParentPath(path);
        var name = path.TrimEnd('/').Split('/').Last();
        try
        {
            var parentDir = await GetObjAsync(driver, dir, ct);
            if (parentDir != null && parentDir.IsDir())
            {
                var siblings = await driver.ListAsync(parentDir, new ListArgs(), ct);
                return siblings.FirstOrDefault(o => o.GetName() == name);
            }
        }
        catch { }

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
