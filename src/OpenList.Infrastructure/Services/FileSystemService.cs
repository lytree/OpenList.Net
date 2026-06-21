using OpenList.Core.Drivers;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Zio;

namespace OpenList.Infrastructure.Services;

public class FileSystemService : IFileSystemService
{
    private readonly ZioMountService _mountService;
    private readonly IMemoryCache _cache;
    private readonly ILogger<FileSystemService> _logger;
    private readonly IServiceProvider _serviceProvider;

    public FileSystemService(ZioMountService mountService, IMemoryCache cache, ILogger<FileSystemService> logger, IServiceProvider serviceProvider)
    {
        _mountService = mountService;
        _cache = cache;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public async Task<List<IObj>> ListAsync(string path, ListArgs? args = null, CancellationToken ct = default)
    {
        args ??= new ListArgs();

        // Go: virtualFiles are always computed for any path, then merged with actual driver results
        var virtualFiles = _mountService.GetVirtualFilesByPath(path, args.WithStorageDetails, ct);

        var (fs, resolvedPath, storage) = _mountService.Resolve(path);
        List<IObj> driverObjs = [];

        if (fs != null && storage != null)
        {
            var config = (fs as IFileSystemDriverMeta)?.Config;
            var cacheKey = $"dir:{storage.MountPath}:{resolvedPath}";

            if (!args.Refresh && config is { NoCache: false })
            {
                if (_cache.TryGetValue(cacheKey, out List<IObj>? cached) && cached != null)
                    driverObjs = cached;
                else
                {
                    driverObjs = ListFromFs(fs, resolvedPath, storage);
                    if (config is { LocalSort: true })
                        SortFiles(driverObjs, storage.OrderBy, storage.OrderDirection);
                    ExtractFolder(driverObjs, storage.ExtractFolder);

                    if (config is { NoCache: false })
                        _cache.Set(cacheKey, driverObjs, TimeSpan.FromMinutes(Math.Max(storage.CacheExpiration, 1)));
                }
            }
            else
            {
                driverObjs = ListFromFs(fs, resolvedPath, storage);
                if (config is { LocalSort: true })
                    SortFiles(driverObjs, storage.OrderBy, storage.OrderDirection);
                ExtractFolder(driverObjs, storage.ExtractFolder);

                if (config is { NoCache: false })
                    _cache.Set(cacheKey, driverObjs, TimeSpan.FromMinutes(Math.Max(storage.CacheExpiration, 1)));
            }
        }

        // Merge: virtual files override driver objects with the same name (Go: om.Merge)
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
            var virtualFiles = _mountService.GetVirtualFilesByPath(parentDir);
            var virtualMatch = virtualFiles.FirstOrDefault(v => v.GetName() == name);
            if (virtualMatch != null) return virtualMatch;
        }

        var (fs, resolvedPath, storage) = _mountService.Resolve(path);
        if (fs == null || storage == null)
        {
            // Root path: return virtual root object (Go: model.Object{Name: "root", IsFolder: true})
            if (path == "/")
                return new ObjItem { Name = "root", Path = "/", IsFolder = true, Mask = ObjMask.ReadOnly | ObjMask.Virtual };
            return null;
        }

        return GetObjFromFs(fs, resolvedPath, storage);
    }

    public async Task<Link?> LinkAsync(string path, LinkArgs args, CancellationToken ct = default)
    {
        var (fs, resolvedPath, storage) = _mountService.Resolve(path);
        if (fs == null || storage == null) return null;
        if (fs is not IFileSystemLinkProvider linkProvider) return null;

        // Check if it's a file (not directory)
        if (!fs.FileExists(resolvedPath)) return null;

        // Check link cache
        var cacheKey = $"link:{storage.MountPath}:{resolvedPath}";
        var config = (fs as IFileSystemDriverMeta)?.Config;
        if (config is { NoCache: false } && _cache.TryGetValue(cacheKey, out Link? cachedLink))
            return cachedLink;

        var link = await linkProvider.GetLinkAsync(resolvedPath, args, ct);

        // Cache link for 30 minutes
        if (link != null && config is { NoCache: false })
            _cache.Set(cacheKey, link, TimeSpan.FromMinutes(30));

        return link;
    }

    public Task MakeDirAsync(string path, CancellationToken ct = default)
    {
        var (fs, resolvedPath, storage) = _mountService.Resolve(path);
        if (fs == null) return Task.CompletedTask;

        try
        {
            fs.CreateDirectory(resolvedPath);
            InvalidateDirectoryCache(storage, GetParentPath(resolvedPath.ToString()));
        }
        catch (NotSupportedException) { }
        return Task.CompletedTask;
    }

    public Task MoveAsync(string srcPath, string dstDir, CancellationToken ct = default)
    {
        var (srcFs, srcResolved, srcStorage) = _mountService.Resolve(srcPath);
        var (dstFs, dstResolved, dstStorage) = _mountService.Resolve(dstDir);
        if (srcFs == null || srcFs != dstFs) return Task.CompletedTask;

        var fileName = srcPath.TrimEnd('/').Split('/').Last();
        var dstPath = UPath.Combine(dstResolved, fileName);

        try
        {
            if (srcFs.DirectoryExists(srcResolved))
                srcFs.MoveDirectory(srcResolved, dstPath);
            else
                srcFs.MoveFile(srcResolved, dstPath);

            InvalidateDirectoryCache(srcStorage, GetParentPath(srcResolved.ToString()));
            InvalidateDirectoryCache(dstStorage, dstResolved.ToString());
            InvalidateLinkCache(srcStorage, srcResolved.ToString());
        }
        catch (NotSupportedException) { }
        return Task.CompletedTask;
    }

    public Task CopyAsync(string srcPath, string dstDir, CancellationToken ct = default)
    {
        var (srcFs, srcResolved, _) = _mountService.Resolve(srcPath);
        var (dstFs, dstResolved, dstStorage) = _mountService.Resolve(dstDir);
        if (srcFs == null || srcFs != dstFs) return Task.CompletedTask;

        var fileName = srcPath.TrimEnd('/').Split('/').Last();
        var dstPath = UPath.Combine(dstResolved, fileName);

        try
        {
            if (srcFs.FileExists(srcResolved))
            {
                srcFs.CopyFile(srcResolved, dstPath, overwrite: true);
                InvalidateDirectoryCache(dstStorage, dstResolved.ToString());
            }
        }
        catch (NotSupportedException) { }
        return Task.CompletedTask;
    }

    public Task MergeAsync(string srcPath, string dstDir, CancellationToken ct = default)
    {
        // Go: fs.Merge uses transfer(merge, ...) which recursively merges directory contents
        // At the driver level, merge uses the same CopyFile interface
        return CopyAsync(srcPath, dstDir, ct);
    }

    public Task RenameAsync(string path, string newName, CancellationToken ct = default)
    {
        var (fs, resolvedPath, storage) = _mountService.Resolve(path);
        if (fs == null) return Task.CompletedTask;

        var parentPath = resolvedPath.GetDirectory();
        var newPath = UPath.Combine(parentPath, newName);

        try
        {
            if (fs.DirectoryExists(resolvedPath))
                fs.MoveDirectory(resolvedPath, newPath);
            else
                fs.MoveFile(resolvedPath, newPath);

            InvalidateDirectoryCache(storage, parentPath.ToString());
            InvalidateLinkCache(storage, resolvedPath.ToString());
        }
        catch (NotSupportedException) { }
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string path, CancellationToken ct = default)
    {
        var (fs, resolvedPath, storage) = _mountService.Resolve(path);
        if (fs == null) return Task.CompletedTask;

        try
        {
            if (fs.DirectoryExists(resolvedPath))
                fs.DeleteDirectory(resolvedPath, isRecursive: true);
            else if (fs.FileExists(resolvedPath))
                fs.DeleteFile(resolvedPath);

            InvalidateDirectoryCache(storage, GetParentPath(resolvedPath.ToString()));
            InvalidateLinkCache(storage, resolvedPath.ToString());
        }
        catch (NotSupportedException) { }
        return Task.CompletedTask;
    }

    public async Task PutAsync(string dstDir, FileStreamer file, Action<double> progress, CancellationToken ct = default)
    {
        var (fs, resolvedPath, storage) = _mountService.Resolve(dstDir);
        if (fs == null) return;

        if (fs is IFileSystemUploader uploader)
        {
            await uploader.UploadAsync(resolvedPath, file.Stream!, file.Obj.GetName(), progress, ct);
            InvalidateDirectoryCache(storage, resolvedPath.ToString());
        }
        else
        {
            // Fallback: use OpenFile to write the stream directly
            try
            {
                var filePath = UPath.Combine(resolvedPath, file.Obj.GetName());
                using var stream = fs.OpenFile(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                await file.Stream!.CopyToAsync(stream, 81920, ct);
                InvalidateDirectoryCache(storage, resolvedPath.ToString());
            }
            catch (NotSupportedException) { }
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

    public Task<object?> OtherAsync(OtherArgs args, CancellationToken ct = default)
    {
        // No Zio equivalent — return null
        return Task.FromResult<object?>(null);
    }

    public Task<Storage?> GetStorageAsync(string path, CancellationToken ct = default)
    {
        return Task.FromResult(_mountService.FindStorageByPath(path));
    }

    public async Task<StorageDetails?> GetStorageDetailsAsync(string path, CancellationToken ct = default)
    {
        var (fs, _, storage) = _mountService.Resolve(path);
        if (fs is not IFileSystemWithDetails detailDriver) return null;

        // Check cache (Go: detailCache with 30min TTL)
        var cacheKey = $"detail:{storage?.MountPath}";
        if (_cache.TryGetValue(cacheKey, out StorageDetails? cached))
            return cached;

        var details = await detailDriver.GetDetailsAsync(ct);
        if (details != null)
            _cache.Set(cacheKey, details, TimeSpan.FromMinutes(30));

        return details;
    }

    // ── Archive operations (Go: fs.ArchiveMeta, fs.ArchiveList, fs.ArchiveDecompress) ──

    public async Task<ArchiveMetaResult?> GetArchiveMetaAsync(string path, ArchiveArgs args, CancellationToken ct = default)
    {
        var (fs, resolvedPath, _) = _mountService.Resolve(path);
        if (fs is not IFileSystemArchiveReader reader) return null;

        if (!fs.FileExists(resolvedPath)) return null;

        var meta = await reader.GetArchiveMetaAsync(resolvedPath, args, ct);

        // Build tree from flat entries (Go: archive.BuildTree)
        var tree = BuildArchiveTree(meta.Entries);

        return new ArchiveMetaResult
        {
            Comment = "",
            IsEncrypted = meta.IsEncrypted,
            Content = tree,
            DriverProviding = fs is IFileSystemArchiveDecompress
        };
    }

    public async Task<(List<IObj> Items, int Total)> ListArchiveAsync(string path, ArchiveListArgs args, CancellationToken ct = default)
    {
        var (fs, resolvedPath, _) = _mountService.Resolve(path);
        if (fs is not IFileSystemArchiveReader reader) return ([], 0);

        if (!fs.FileExists(resolvedPath)) return ([], 0);

        var items = (await reader.ListArchiveAsync(resolvedPath, args.ArchiveInnerArgs, ct))
            .Select(item => new ObjItem
            {
                Name = item.Path.GetName(),
                Size = item.Length,
                IsFolder = item.IsDirectory,
                Path = item.Path.FullName,
                Modified = item.LastWriteTime.LocalDateTime
            })
            .Cast<IObj>()
            .ToList();

        var total = items.Count;

        // Pagination (Go: pagination helper)
        if (args.PerPage > 0 && args.PerPage < int.MaxValue)
        {
            var skip = (args.Page - 1) * args.PerPage;
            items = items.Skip(skip).Take(args.PerPage).ToList();
        }

        return (items, total);
    }

    public async Task<TaskInfo?> ArchiveDecompressAsync(string srcPath, string dstDir, ArchiveDecompressArgs args, CancellationToken ct = default)
    {
        var (srcFs, srcResolved, _) = _mountService.Resolve(srcPath);
        var (dstFs, dstResolved, _) = _mountService.Resolve(dstDir);
        if (srcFs is not IFileSystemArchiveDecompress decompressor) return null;

        if (!srcFs.FileExists(srcResolved)) return null;
        if (!dstFs.DirectoryExists(dstResolved)) return null;

        // Create task wrapping decompressor.ArchiveDecompressAsync (Go: fs.ArchiveDecompress creates task)
        var taskService = _serviceProvider.GetService<ITaskService>();
        if (taskService == null) return null;

        var fileName = srcResolved.GetName();
        var task = await taskService.CreateTaskAsync("decompress", $"Decompress {fileName}", async (taskId, innerCt) =>
        {
            await decompressor.ArchiveDecompressAsync(srcResolved, dstResolved, args, innerCt);
        }, ct);

        return task;
    }

    // ── Helpers ──

    private List<IObj> ListFromFs(IFileSystem fs, UPath path, Storage storage)
    {
        try
        {
            var items = fs.EnumerateItems(path, SearchOption.TopDirectoryOnly);
            var result = new List<IObj>();
            foreach (var item in items)
            {
                result.Add(FileSystemItemToObj(item, storage));
            }
            return result;
        }
        catch (NotSupportedException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }

    private static IObj FileSystemItemToObj(FileSystemItem item, Storage storage)
    {
        return new ObjItem
        {
            Name = item.Path.GetName(),
            Path = item.Path.FullName,
            Size = !item.IsDirectory ? item.Length : 0,
            IsFolder = item.IsDirectory,
            Modified = item.LastWriteTime.LocalDateTime,
            CTime = item.CreationTime.LocalDateTime
        };
    }

    private IObj? GetObjFromFs(IFileSystem fs, UPath path, Storage storage)
    {
        try
        {
            if (fs.FileExists(path))
            {
                return new ObjItem
                {
                    Name = path.GetName(),
                    Path = path.FullName,
                    Size = fs.GetFileLength(path),
                    IsFolder = false,
                    Modified = fs.GetLastWriteTime(path),
                    CTime = fs.GetCreationTime(path)
                };
            }
            if (fs.DirectoryExists(path))
            {
                return new ObjItem
                {
                    Name = path.GetName(),
                    Path = path.FullName,
                    IsFolder = true,
                    Modified = fs.GetLastWriteTime(path),
                    CTime = fs.GetCreationTime(path)
                };
            }
        }
        catch (NotSupportedException) { }
        return null;
    }

    /// <summary>
    /// Build tree from flat archive entries (Go: archive.BuildTree).
    /// </summary>
    private static List<ArchiveContentEntry> BuildArchiveTree(List<ArchiveEntry> entries)
    {
        if (entries.Count == 0) return [];

        var hasPaths = entries.Any(e => !string.IsNullOrEmpty(e.InnerPath));
        if (!hasPaths)
        {
            return entries.Select(e => new ArchiveContentEntry
            {
                Name = e.Name,
                Size = e.Size,
                IsDir = e.IsDir,
                Modified = e.Modified,
                Type = ObjTypeHelper.GetObjType(e.Name, e.IsDir)
            }).ToList();
        }

        var root = new List<ArchiveContentEntry>();
        var dirMap = new Dictionary<string, ArchiveContentEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries.OrderBy(e => e.InnerPath ?? e.Name))
        {
            var path = (entry.InnerPath ?? entry.Name).TrimStart('/');
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            var node = new ArchiveContentEntry
            {
                Name = parts[^1],
                Size = entry.Size,
                IsDir = entry.IsDir,
                Modified = entry.Modified,
                Type = ObjTypeHelper.GetObjType(parts[^1], entry.IsDir)
            };

            if (parts.Length == 1)
            {
                root.Add(node);
                if (entry.IsDir)
                    dirMap[path.TrimEnd('/')] = node;
            }
            else
            {
                var parentPath = string.Join('/', parts[..^1]);
                if (dirMap.TryGetValue(parentPath, out var parent))
                {
                    parent.Children.Add(node);
                }
                else
                {
                    ArchiveContentEntry? lastParent = null;
                    var buildPath = "";
                    for (int i = 0; i < parts.Length - 1; i++)
                    {
                        buildPath = i == 0 ? parts[0] : buildPath + "/" + parts[i];
                        if (!dirMap.TryGetValue(buildPath, out var intermediate))
                        {
                            intermediate = new ArchiveContentEntry
                            {
                                Name = parts[i],
                                IsDir = true,
                                Type = (int)ObjType.Folder
                            };
                            if (lastParent != null)
                                lastParent.Children.Add(intermediate);
                            else
                                root.Add(intermediate);
                            dirMap[buildPath] = intermediate;
                        }
                        lastParent = intermediate;
                    }
                    lastParent?.Children.Add(node);
                }

                if (entry.IsDir)
                    dirMap[path.TrimEnd('/')] = node;
            }
        }

        return root;
    }

    private void InvalidateDirectoryCache(Storage? storage, string dirPath)
    {
        if (storage == null) return;
        var cacheKey = $"dir:{storage.MountPath}:{dirPath}";
        _cache.Remove(cacheKey);
    }

    private void InvalidateLinkCache(Storage? storage, string filePath)
    {
        if (storage == null) return;
        var cacheKey = $"link:{storage.MountPath}:{filePath}";
        _cache.Remove(cacheKey);
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
