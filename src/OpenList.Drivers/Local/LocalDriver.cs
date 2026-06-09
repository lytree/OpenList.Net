using System.Text.Json;
using FluentStorage;
using FluentStorage.Blobs;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;
using OpenList.Core.Interfaces;

namespace OpenList.Drivers.Local;

public class LocalDriver : RootPathDriver,
    IDriverGetter, IDriverMkdirResult, IDriverMove, IDriverRename,
    IDriverCopy, IDriverRemove, IDriverPutResult, IDriverWithDetails
{
    private IBlobStorage _blobStorage = null!;

    public override DriverConfig Config() => new()
    {
        Name = "Local",
        LocalSort = true,
        OnlyProxy = true,
        NoCache = true,
        DefaultRoot = "/",
        NoLinkURL = true,
        CheckStatus = true
    };

    public override DriverInfo Info() => new()
    {
        Common =
        [
            new DriverItem { Name = "root_folder_path", Type = "string", Default = "/", Required = true, Help = "Root folder path" }
        ],
        Additional =
        [
            new DriverItem { Name = "show_hidden", Type = "bool", Default = "true", Required = false, Help = "show hidden directories and files" },
            new DriverItem { Name = "directory_size", Type = "bool", Default = "false", Required = false, Help = "This might impact host performance" },
            new DriverItem { Name = "thumbnail", Type = "bool", Default = "false", Required = true, Help = "enable thumbnail" },
            new DriverItem { Name = "thumb_cache_folder", Type = "string", Required = false, Help = "Thumbnail cache folder" },
            new DriverItem { Name = "thumb_concurrency", Type = "string", Default = "16", Required = false, Help = "Number of concurrent thumbnail generation goroutines" },
            new DriverItem { Name = "video_thumb_pos", Type = "string", Default = "20%", Required = false, Help = "The position of the video thumbnail" },
            new DriverItem { Name = "mkdir_perm", Type = "string", Default = "777", Help = "Mkdir permission (octal)" },
            new DriverItem { Name = "recycle_bin_path", Type = "string", Default = "delete permanently", Help = "path to recycle bin, delete permanently if empty or keep 'delete permanently'" }
        ],
        Config = Config()
    };

    public override object GetAddition() => JsonSerializer.Deserialize<LocalAddition>(_storage.Addition) ?? new LocalAddition();

    public override Task InitAsync(CancellationToken ct = default)
    {
        var addition = (LocalAddition)GetAddition();
        RootFolderPath = addition.RootFolderPath;

        if (!Directory.Exists(RootFolderPath))
            throw new DirectoryNotFoundException($"Root folder {RootFolderPath} not exists");

        if (!Path.IsPathRooted(RootFolderPath))
            RootFolderPath = Path.GetFullPath(RootFolderPath);

        // Initialize FluentStorage local disk provider
        _blobStorage = StorageFactory.Blobs.DirectoryFiles(RootFolderPath);

        if (!string.IsNullOrEmpty(addition.ThumbCacheFolder) && !Directory.Exists(addition.ThumbCacheFolder))
            Directory.CreateDirectory(addition.ThumbCacheFolder);

        return Task.CompletedTask;
    }

    public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;

    public override async Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default)
    {
        var addition = (LocalAddition)GetAddition();
        var virtualPath = dir.GetPath();
        var relativePath = ToRelativePath(virtualPath);

        var result = new List<IObj>();

        // Use FluentStorage to list blobs
        var blobs = await _blobStorage.ListAsync(new ListOptions { FolderPath = relativePath, BrowseFilter = null }, ct);

        foreach (var blob in blobs)
        {
            if (blob.IsFolder)
            {
                // Skip hidden if configured
                if (!addition.ShowHidden && IsHiddenName(blob.Name)) continue;

                var size = 0L;
                if (addition.DirectorySize)
                {
                    try { size = GetDirectorySize(GetActualPath(virtualPath + "/" + blob.Name)); } catch { }
                }

                result.Add(new ObjItem
                {
                    Name = blob.Name,
                    Size = blob.Size ?? 0,
                    Modified = blob.LastModificationTime?.UtcDateTime ?? DateTime.MinValue,
                    CTime = GetCreationTime(GetActualPath(virtualPath + "/" + blob.Name)),
                    IsFolder = true,
                    Path = virtualPath + "/" + blob.Name
                });
            }
            else
            {
                if (!addition.ShowHidden && IsHiddenName(blob.Name)) continue;

                result.Add(new ObjItem
                {
                    Name = blob.Name,
                    Size = blob.Size ?? 0,
                    Modified = blob.LastModificationTime?.UtcDateTime ?? DateTime.MinValue,
                    CTime = GetCreationTime(GetActualPath(virtualPath + "/" + blob.Name)),
                    IsFolder = false,
                    Path = virtualPath + "/" + blob.Name
                });
            }
        }

        return result;
    }

    public override Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default)
    {
        var path = GetActualPath(file.GetPath());
        if (!File.Exists(path)) return Task.FromResult<Link?>(null);

        var link = new Link { URL = path };

        if (args.Type == "thumb")
        {
            var addition = (LocalAddition)GetAddition();
            if (addition.Thumbnail)
            {
                var thumbPath = GetThumbnailPath(file, addition);
                if (thumbPath != null && File.Exists(thumbPath))
                {
                    return Task.FromResult<Link?>(new Link
                    {
                        URL = thumbPath,
                        Header = new Dictionary<string, string> { { "Content-Type", "image/png" } }
                    });
                }
            }
        }

        return Task.FromResult<Link?>(link);
    }

    public async Task<IObj?> GetAsync(string path, CancellationToken ct = default)
    {
        var addition = (LocalAddition)GetAddition();
        var actualPath = GetActualPath(path);
        var relativePath = ToRelativePath(path);

        // Try to get blob info from FluentStorage
        var blob = await _blobStorage.GetBlobAsync(relativePath, ct);
        if (blob != null)
        {
            var size = blob.Size ?? 0;
            if (blob.IsFolder && addition.DirectorySize)
            {
                try { size = GetDirectorySize(actualPath); } catch { }
            }

            return new ObjItem
            {
                Name = blob.Name,
                Size = size,
                Modified = blob.LastModificationTime?.UtcDateTime ?? DateTime.MinValue,
                CTime = GetCreationTime(actualPath),
                IsFolder = blob.IsFolder,
                Path = path
            };
        }

        // Fallback to direct file system check (for root path etc.)
        if (File.Exists(actualPath))
        {
            var fi = new FileInfo(actualPath);
            return new ObjItem
            {
                Name = fi.Name, Size = fi.Length, Modified = fi.LastWriteTimeUtc,
                CTime = fi.CreationTimeUtc, IsFolder = false, Path = path
            };
        }
        if (Directory.Exists(actualPath))
        {
            var di = new DirectoryInfo(actualPath);
            var size = 0L;
            if (addition.DirectorySize)
            {
                try { size = GetDirectorySize(di.FullName); } catch { }
            }
            return new ObjItem
            {
                Name = di.Name, Size = size, Modified = di.LastWriteTimeUtc,
                CTime = di.CreationTimeUtc, IsFolder = true, Path = path
            };
        }

        return null;
    }

    public Task<IObj> MakeDirAsync(IObj parentDir, string dirName, CancellationToken ct = default)
    {
        var addition = (LocalAddition)GetAddition();
        var path = Path.Join(GetActualPath(parentDir.GetPath()), dirName);

        // Parse octal permission
        var perm = ParseOctalPermission(addition.MkdirPerm);
        Directory.CreateDirectory(path);
        if (perm > 0)
        {
            try
            {
                var dirInfo = new DirectoryInfo(path);
                dirInfo.Attributes = (FileAttributes)perm;
            }
            catch { }
        }

        var di = new DirectoryInfo(path);
        return Task.FromResult<IObj>(new ObjItem
        {
            Name = di.Name, IsFolder = true, Modified = di.LastWriteTimeUtc,
            CTime = di.CreationTimeUtc, Path = parentDir.GetPath() + "/" + dirName
        });
    }

    public Task MoveAsync(IObj srcObj, IObj dstDir, CancellationToken ct = default)
    {
        var srcPath = GetActualPath(srcObj.GetPath());
        var dstPath = Path.Join(GetActualPath(dstDir.GetPath()), srcObj.GetName());

        if (dstPath.StartsWith(srcPath + Path.DirectorySeparatorChar))
            throw new InvalidOperationException("The destination folder is a subfolder of the source folder");

        try
        {
            if (srcObj.IsDir()) Directory.Move(srcPath, dstPath);
            else File.Move(srcPath, dstPath);
        }
        catch (IOException ex) when (IsCrossDeviceError(ex))
        {
            throw new NotImplementedException("Cross-device move requires task-based processing");
        }
        return Task.CompletedTask;
    }

    public Task RenameAsync(IObj srcObj, string newName, CancellationToken ct = default)
    {
        var srcPath = GetActualPath(srcObj.GetPath());
        var dir = Path.GetDirectoryName(srcPath)!;
        var dstPath = Path.Join(dir, newName);
        if (srcObj.IsDir()) Directory.Move(srcPath, dstPath);
        else File.Move(srcPath, dstPath);
        return Task.CompletedTask;
    }

    public Task CopyAsync(IObj srcObj, IObj dstDir, CancellationToken ct = default)
    {
        var srcPath = GetActualPath(srcObj.GetPath());
        var dstPath = Path.Join(GetActualPath(dstDir.GetPath()), srcObj.GetName());

        if (dstPath.StartsWith(srcPath + Path.DirectorySeparatorChar))
            throw new InvalidOperationException("The destination folder is a subfolder of the source folder");

        if (srcObj.IsDir()) CopyDirectory(srcPath, dstPath);
        else File.Copy(srcPath, dstPath, true);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(IObj obj, CancellationToken ct = default)
    {
        var addition = (LocalAddition)GetAddition();
        var path = GetActualPath(obj.GetPath());

        // Recycle bin support (Go: checks for empty or "delete permanently")
        if (!string.IsNullOrEmpty(addition.RecycleBinPath) &&
            addition.RecycleBinPath != "delete permanently")
        {
            try
            {
                var relPath = Path.GetRelativePath(RootFolderPath, Path.GetDirectoryName(path)!);
                var recycleDir = Path.Join(addition.RecycleBinPath, relPath);
                if (!Directory.Exists(recycleDir))
                    Directory.CreateDirectory(recycleDir);

                var dstPath = Path.Join(recycleDir, obj.GetName());
                if (File.Exists(dstPath) || Directory.Exists(dstPath))
                    dstPath = Path.Join(recycleDir, $"{obj.GetName()}_{DateTime.Now:yyyyMMddHHmmss}");

                if (obj.IsDir()) Directory.Move(path, dstPath);
                else File.Move(path, dstPath);
                return Task.CompletedTask;
            }
            catch
            {
                // Fall through to permanent delete
            }
        }

        // Permanent delete
        if (obj.IsDir()) Directory.Delete(path, true);
        else File.Delete(path);
        return Task.CompletedTask;
    }

    public async Task<IObj> PutAsync(IObj dstDir, FileStreamer file, Action<double> updateProgress, CancellationToken ct = default)
    {
        var dirPath = GetActualPath(dstDir.GetPath());
        var filePath = Path.Join(dirPath, file.Obj.GetName());
        Directory.CreateDirectory(dirPath);

        // Use FluentStorage to write
        var relativePath = ToRelativePath(dstDir.GetPath() + "/" + file.Obj.GetName());
        if (file.Stream != null)
        {
            await _blobStorage.WriteAsync(relativePath, file.Stream, append: false, cancellationToken: ct);
        }

        var fi = new FileInfo(filePath);
        return new ObjItem
        {
            Name = fi.Name, Size = fi.Length, Modified = fi.LastWriteTimeUtc,
            CTime = fi.CreationTimeUtc, IsFolder = false, Path = dstDir.GetPath() + "/" + file.Obj.GetName()
        };
    }

    public Task<StorageDetails?> GetDetailsAsync(CancellationToken ct = default)
    {
        try
        {
            var root = Path.GetPathRoot(RootFolderPath);
            if (string.IsNullOrEmpty(root)) return Task.FromResult<StorageDetails?>(null);
            var driveInfo = new DriveInfo(root);
            return Task.FromResult<StorageDetails?>(new StorageDetails
            {
                TotalSpace = driveInfo.TotalSize,
                UsedSpace = driveInfo.TotalSize - driveInfo.AvailableFreeSpace
            });
        }
        catch
        {
            return Task.FromResult<StorageDetails?>(null);
        }
    }

    private string GetActualPath(string virtualPath)
    {
        var root = RootFolderPath;
        if (string.IsNullOrEmpty(root)) root = "/";
        var relative = virtualPath.TrimStart('/');
        return string.IsNullOrEmpty(relative) ? root : Path.Join(root, relative);
    }

    /// <summary>Convert virtual path to relative path for FluentStorage</summary>
    private string ToRelativePath(string virtualPath)
    {
        var relative = virtualPath.TrimStart('/');
        return string.IsNullOrEmpty(relative) ? "/" : relative;
    }

    private static bool IsHiddenName(string name)
    {
        return name.StartsWith('.') && name != "." && name != "..";
    }

    private static bool IsCrossDeviceError(IOException ex)
    {
        return ex.HResult == 17 ||
               ex.HResult == -2147024890 ||
               ex.Message.Contains("different") ||
               ex.Message.Contains("cross-device");
    }

    private static long GetDirectorySize(string path)
    {
        var size = 0L;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { size += new FileInfo(file).Length; } catch { }
            }
        }
        catch { }
        return size;
    }

    private static DateTime GetCreationTime(string path)
    {
        try
        {
            if (File.Exists(path)) return File.GetCreationTimeUtc(path);
            if (Directory.Exists(path)) return Directory.GetCreationTimeUtc(path);
        }
        catch { }
        return DateTime.MinValue;
    }

    private static int ParseOctalPermission(string? perm)
    {
        if (string.IsNullOrEmpty(perm)) return 0;
        try
        {
            return Convert.ToInt32(perm, 8);
        }
        catch
        {
            return 0;
        }
    }

    private string? GetThumbnailPath(IObj file, LocalAddition addition)
    {
        if (string.IsNullOrEmpty(addition.ThumbCacheFolder)) return null;
        var ext = Path.GetExtension(file.GetName()).ToLowerInvariant();
        var isImage = ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp";
        var isVideo = ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".flv" or ".webm";
        if (!isImage && !isVideo) return null;

        var hash = file.GetName().GetHashCode().ToString("x");
        return Path.Join(addition.ThumbCacheFolder, $"{hash}.png");
    }

    private static void CopyDirectory(string srcDir, string dstDir)
    {
        Directory.CreateDirectory(dstDir);
        foreach (var file in Directory.GetFiles(srcDir))
            File.Copy(file, Path.Join(dstDir, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(srcDir))
            CopyDirectory(dir, Path.Join(dstDir, Path.GetFileName(dir)));
    }
}

public class LocalAddition
{
    public string RootFolderPath { get; set; } = "/";
    public bool ShowHidden { get; set; } = true;
    public bool DirectorySize { get; set; }
    public bool Thumbnail { get; set; }
    public string? ThumbCacheFolder { get; set; }
    public string? ThumbConcurrency { get; set; } = "16";
    public string? VideoThumbPos { get; set; } = "20%";
    public string? MkdirPerm { get; set; } = "777";
    public string? RecycleBinPath { get; set; } = "delete permanently";
}
