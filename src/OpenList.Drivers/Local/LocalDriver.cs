using System.Text.Json;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;
using OpenList.Core.Interfaces;

namespace OpenList.Drivers.Local;

public class LocalDriver : RootPathDriver,
    IDriverGetter, IDriverMkdirResult, IDriverMove, IDriverRename,
    IDriverCopy, IDriverRemove, IDriverPutResult, IDriverWithDetails
{
    public override DriverConfig Config() => new()
    {
        Name = "Local",
        LocalSort = true,
        OnlyProxy = true,
        NoCache = true,
        DefaultRoot = "/",
        NoLinkURL = true
    };

    public override object GetAddition() => JsonSerializer.Deserialize<LocalAddition>(_storage.Addition) ?? new LocalAddition();
    protected override Type? GetAdditionType() => typeof(LocalAddition);

    public override Task InitAsync(CancellationToken ct = default)
    {
        var addition = (LocalAddition)GetAddition();
        RootFolderPath = addition.RootFolderPath;

        if (!Directory.Exists(RootFolderPath))
            throw new DirectoryNotFoundException($"Root folder {RootFolderPath} not exists");

        if (!Path.IsPathRooted(RootFolderPath))
            RootFolderPath = Path.GetFullPath(RootFolderPath);

        if (!string.IsNullOrEmpty(addition.ThumbCacheFolder) && !Directory.Exists(addition.ThumbCacheFolder))
            Directory.CreateDirectory(addition.ThumbCacheFolder);

        return Task.CompletedTask;
    }

    public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// List directory contents. Mirrors Go: dir.GetPath() returns actual filesystem path.
    /// Go: fullPath := dir.GetPath() → readDir(fullPath)
    /// </summary>
    public override Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default)
    {
        var addition = (LocalAddition)GetAddition();
        // Go: fullPath := dir.GetPath() — this is the actual filesystem path
        var fullPath = dir.GetPath();

        var result = new List<IObj>();

        // Read directory entries directly (Go: readDir)
        var dirInfo = new DirectoryInfo(fullPath);
        if (!dirInfo.Exists)
            return Task.FromResult(result);

        foreach (var entry in dirInfo.EnumerateFileSystemInfos())
        {
            // Skip hidden if configured
            if (!addition.ShowHidden && IsHiddenName(entry.Name)) continue;

            var isFolder = entry is DirectoryInfo;
            // Go: isSymlinkDir check — check if symlink points to a directory
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && entry is FileInfo)
            {
                try
                {
                    isFolder = File.GetAttributes(entry.FullName).HasFlag(FileAttributes.Directory);
                }
                catch { }
            }

            var size = 0L;
            if (isFolder)
            {
                if (addition.DirectorySize)
                {
                    try { size = GetDirectorySize(Path.Join(fullPath, entry.Name)); } catch { }
                }
            }
            else
            {
                try { size = ((FileInfo)entry).Length; } catch { }
            }

            var ctime = DateTime.MinValue;
            try { ctime = entry.CreationTimeUtc; } catch { }

            // Go: Path = filepath.Join(fullPath, f.Name()) — actual filesystem path
            result.Add(new ObjItem
            {
                Name = entry.Name,
                Size = size,
                Modified = entry.LastWriteTimeUtc,
                CTime = ctime,
                IsFolder = isFolder,
                Path = Path.Join(fullPath, entry.Name)
            });
        }

        return Task.FromResult(result);
    }

    /// <summary>
    /// Get a single object by path. Mirrors Go: path = filepath.Join(d.GetRootPath(), path)
    /// </summary>
    public Task<IObj?> GetAsync(string path, CancellationToken ct = default)
    {
        var addition = (LocalAddition)GetAddition();
        // Go: path = filepath.Join(d.GetRootPath(), path)
        var actualPath = Path.Join(RootFolderPath, path.TrimStart('/'));

        if (File.Exists(actualPath))
        {
            var fi = new FileInfo(actualPath);
            return Task.FromResult<IObj?>(new ObjItem
            {
                Name = fi.Name,
                Size = fi.Length,
                Modified = fi.LastWriteTimeUtc,
                CTime = fi.CreationTimeUtc,
                IsFolder = false,
                Path = actualPath
            });
        }

        if (Directory.Exists(actualPath))
        {
            var di = new DirectoryInfo(actualPath);
            var size = 0L;
            if (addition.DirectorySize)
            {
                try { size = GetDirectorySize(di.FullName); } catch { }
            }
            return Task.FromResult<IObj?>(new ObjItem
            {
                Name = di.Name,
                Size = size,
                Modified = di.LastWriteTimeUtc,
                CTime = di.CreationTimeUtc,
                IsFolder = true,
                Path = actualPath
            });
        }

        return Task.FromResult<IObj?>(null);
    }

    public override Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default)
    {
        // Go: fullPath := file.GetPath() — Path is actual filesystem path
        var fullPath = file.GetPath();
        if (!File.Exists(fullPath)) return Task.FromResult<Link?>(null);

        var link = new Link { URL = fullPath };

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

    /// <summary>
    /// Make directory. Go: fullPath := filepath.Join(parentDir.GetPath(), dirName)
    /// </summary>
    public Task<IObj> MakeDirAsync(IObj parentDir, string dirName, CancellationToken ct = default)
    {
        var addition = (LocalAddition)GetAddition();
        // Go: fullPath := filepath.Join(parentDir.GetPath(), dirName)
        var fullPath = Path.Join(parentDir.GetPath(), dirName);

        var perm = ParseOctalPermission(addition.MkdirPerm);
        Directory.CreateDirectory(fullPath);
        if (perm > 0)
        {
            try
            {
                var dirInfo = new DirectoryInfo(fullPath);
                dirInfo.Attributes = (FileAttributes)perm;
            }
            catch { }
        }

        var di = new DirectoryInfo(fullPath);
        return Task.FromResult<IObj>(new ObjItem
        {
            Name = di.Name,
            IsFolder = true,
            Modified = di.LastWriteTimeUtc,
            CTime = di.CreationTimeUtc,
            Path = fullPath
        });
    }

    /// <summary>
    /// Move. Go: srcPath := srcObj.GetPath(); dstPath := filepath.Join(dstDir.GetPath(), srcObj.GetName())
    /// </summary>
    public Task MoveAsync(IObj srcObj, IObj dstDir, CancellationToken ct = default)
    {
        var srcPath = srcObj.GetPath();
        var dstPath = Path.Join(dstDir.GetPath(), srcObj.GetName());

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

    /// <summary>
    /// Rename. Go: srcPath := srcObj.GetPath(); dstPath := filepath.Join(filepath.Dir(srcPath), newName)
    /// </summary>
    public Task RenameAsync(IObj srcObj, string newName, CancellationToken ct = default)
    {
        var srcPath = srcObj.GetPath();
        var dir = Path.GetDirectoryName(srcPath)!;
        var dstPath = Path.Join(dir, newName);
        if (srcObj.IsDir()) Directory.Move(srcPath, dstPath);
        else File.Move(srcPath, dstPath);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Copy. Go: srcPath := srcObj.GetPath(); dstPath := filepath.Join(dstDir.GetPath(), srcObj.GetName())
    /// </summary>
    public Task CopyAsync(IObj srcObj, IObj dstDir, CancellationToken ct = default)
    {
        var srcPath = srcObj.GetPath();
        var dstPath = Path.Join(dstDir.GetPath(), srcObj.GetName());

        if (dstPath.StartsWith(srcPath + Path.DirectorySeparatorChar))
            throw new InvalidOperationException("The destination folder is a subfolder of the source folder");

        if (srcObj.IsDir()) CopyDirectory(srcPath, dstPath);
        else File.Copy(srcPath, dstPath, true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Remove. Go: obj.GetPath() is actual filesystem path
    /// </summary>
    public Task RemoveAsync(IObj obj, CancellationToken ct = default)
    {
        var addition = (LocalAddition)GetAddition();
        var path = obj.GetPath();

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

    /// <summary>
    /// Put (upload). Go: fullPath := filepath.Join(dstDir.GetPath(), stream.GetName())
    /// </summary>
    public async Task<IObj> PutAsync(IObj dstDir, FileStreamer file, Action<double> updateProgress, CancellationToken ct = default)
    {
        // Go: fullPath := filepath.Join(dstDir.GetPath(), stream.GetName())
        var fullPath = Path.Join(dstDir.GetPath(), file.Obj.GetName());
        Directory.CreateDirectory(dstDir.GetPath());

        if (file.Stream != null)
        {
            using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write);
            await file.Stream.CopyToAsync(fs, ct);
        }

        var fi = new FileInfo(fullPath);
        return new ObjItem
        {
            Name = fi.Name,
            Size = fi.Length,
            Modified = fi.LastWriteTimeUtc,
            CTime = fi.CreationTimeUtc,
            IsFolder = false,
            Path = fullPath
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
    [System.ComponentModel.Description("This might impact host performance")]
    public bool DirectorySize { get; set; }
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.Description("enable thumbnail")]
    public bool Thumbnail { get; set; }
    public string? ThumbCacheFolder { get; set; }
    [System.ComponentModel.Description("Number of concurrent thumbnail generation goroutines. This controls how many thumbnails can be generated in parallel.")]
    public string? ThumbConcurrency { get; set; } = "16";
    [System.ComponentModel.Description("The position of the video thumbnail. If the value is a number (integer or floating point), it represents the time in seconds. If the value ends with '%', it represents the percentage of the video duration.")]
    public string? VideoThumbPos { get; set; } = "20%";
    [System.ComponentModel.Description("show hidden directories and files")]
    public bool ShowHidden { get; set; } = true;
    [System.ComponentModel.Description("Mkdir permission (octal)")]
    public string? MkdirPerm { get; set; } = "777";
    [System.ComponentModel.Description("path to recycle bin, delete permanently if empty or keep 'delete permanently'")]
    public string? RecycleBinPath { get; set; } = "delete permanently";
}
