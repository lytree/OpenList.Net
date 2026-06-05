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
        DefaultRoot = "/",
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
            new DriverItem { Name = "show_hidden", Type = "bool", Default = "true", Help = "Show hidden files" },
            new DriverItem { Name = "recursion", Type = "bool", Default = "false", Help = "Recursive listing" },
            new DriverItem { Name = "directory_size", Type = "bool", Default = "false", Help = "Calculate directory size" },
            new DriverItem { Name = "thumbnail", Type = "bool", Default = "false", Help = "Enable thumbnail" },
            new DriverItem { Name = "thumb_cache_folder", Type = "string", Help = "Thumbnail cache folder" },
            new DriverItem { Name = "thumb_concurrency", Type = "number", Help = "Thumbnail concurrency" },
            new DriverItem { Name = "video_thumb_pos", Type = "string", Help = "Video thumbnail position" },
            new DriverItem { Name = "mkdir_perm", Type = "string", Default = "777", Help = "Mkdir permission (octal)" },
            new DriverItem { Name = "recycle_bin_path", Type = "string", Help = "Recycle bin path" }
        ],
        Config = Config()
    };

    public override object GetAddition() => JsonSerializer.Deserialize<LocalAddition>(_storage.Addition) ?? new LocalAddition();

    public override Task InitAsync(CancellationToken ct = default)
    {
        var addition = (LocalAddition)GetAddition();
        RootFolderPath = addition.RootFolderPath;

        // Validate root path exists
        if (!Directory.Exists(RootFolderPath))
            throw new DirectoryNotFoundException($"Root folder {RootFolderPath} not exists");

        // Resolve to absolute path
        if (!Path.IsPathRooted(RootFolderPath))
            RootFolderPath = Path.GetFullPath(RootFolderPath);

        // Create thumbnail cache folder if configured
        if (!string.IsNullOrEmpty(addition.ThumbCacheFolder) && !Directory.Exists(addition.ThumbCacheFolder))
            Directory.CreateDirectory(addition.ThumbCacheFolder);

        return Task.CompletedTask;
    }

    public override Task DropAsync(CancellationToken ct = default) => Task.CompletedTask;

    public override Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default)
    {
        var addition = (LocalAddition)GetAddition();
        var path = GetActualPath(dir.GetPath());
        if (!Directory.Exists(path)) return Task.FromResult(new List<IObj>());

        var result = new List<IObj>();
        var dirInfo = new DirectoryInfo(path);

        try
        {
            foreach (var d in dirInfo.EnumerateDirectories())
            {
                // Skip hidden files if ShowHidden is false
                if (!addition.ShowHidden && IsHidden(d)) continue;

                var isFolder = true;
                // Handle symlinks - check if symlink target is a directory
                if (d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    try
                    {
                        var target = d.ResolveLinkTarget(true);
                        isFolder = target != null && (target.Attributes & FileAttributes.Directory) != 0;
                    }
                    catch { continue; }
                }

                var size = 0L;
                if (addition.DirectorySize && isFolder)
                {
                    try { size = GetDirectorySize(d.FullName); } catch { }
                }

                result.Add(new ObjItem
                {
                    Name = d.Name,
                    Size = size,
                    Modified = d.LastWriteTimeUtc,
                    CTime = d.CreationTimeUtc,
                    IsFolder = isFolder,
                    Path = dir.GetPath() + "/" + d.Name
                });
            }
        }
        catch (UnauthorizedAccessException) { }

        try
        {
            foreach (var f in dirInfo.EnumerateFiles())
            {
                // Skip hidden files if ShowHidden is false
                if (!addition.ShowHidden && IsHidden(f)) continue;

                result.Add(new ObjItem
                {
                    Name = f.Name,
                    Size = f.Length,
                    Modified = f.LastWriteTimeUtc,
                    CTime = f.CreationTimeUtc,
                    IsFolder = false,
                    Path = dir.GetPath() + "/" + f.Name
                });
            }
        }
        catch (UnauthorizedAccessException) { }

        // Recursion: include subdirectory contents
        if (addition.Recursion)
        {
            var subItems = new List<IObj>();
            foreach (var item in result.Where(o => o.IsDir()).ToList())
            {
                try
                {
                    var subPath = GetActualPath(item.GetPath());
                    var subDirInfo = new DirectoryInfo(subPath);
                    foreach (var sub in subDirInfo.EnumerateFileSystemInfos())
                    {
                        if (!addition.ShowHidden && IsHidden(sub)) continue;
                        var isDir = sub is DirectoryInfo;
                        if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            try
                            {
                                var target = sub.ResolveLinkTarget(true);
                                isDir = target != null && (target.Attributes & FileAttributes.Directory) != 0;
                            }
                            catch { continue; }
                        }
                        subItems.Add(new ObjItem
                        {
                            Name = sub.Name,
                            Size = isDir ? 0 : (sub as FileInfo)?.Length ?? 0,
                            Modified = sub.LastWriteTimeUtc,
                            CTime = sub.CreationTimeUtc,
                            IsFolder = isDir,
                            Path = item.GetPath() + "/" + sub.Name
                        });
                    }
                }
                catch (UnauthorizedAccessException) { }
            }
            result.AddRange(subItems);
        }

        return Task.FromResult(result);
    }

    public override Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default)
    {
        var path = GetActualPath(file.GetPath());
        if (!File.Exists(path)) return Task.FromResult<Link?>(null);

        var link = new Link { URL = path };

        // Handle thumbnail request
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

    public Task<IObj?> GetAsync(string path, CancellationToken ct = default)
    {
        var addition = (LocalAddition)GetAddition();
        var actualPath = GetActualPath(path);
        if (File.Exists(actualPath))
        {
            var fi = new FileInfo(actualPath);
            return Task.FromResult<IObj?>(new ObjItem
            {
                Name = fi.Name, Size = fi.Length, Modified = fi.LastWriteTimeUtc,
                CTime = fi.CreationTimeUtc, IsFolder = false, Path = path
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
                Name = di.Name, Size = size, Modified = di.LastWriteTimeUtc,
                CTime = di.CreationTimeUtc, IsFolder = true, Path = path
            });
        }
        return Task.FromResult<IObj?>(null);
    }

    public Task<IObj> MakeDirAsync(IObj parentDir, string dirName, CancellationToken ct = default)
    {
        var addition = (LocalAddition)GetAddition();
        var path = Path.Join(GetActualPath(parentDir.GetPath()), dirName);
        var perm = addition.MkdirPerm;
        if (perm > 0)
        {
            Directory.CreateDirectory(path);
            try
            {
                var dirInfo = new DirectoryInfo(path);
                dirInfo.Attributes = (FileAttributes)perm;
            }
            catch { }
        }
        else
        {
            Directory.CreateDirectory(path);
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

        // Prevent moving to subfolder of itself
        if (dstPath.StartsWith(srcPath + Path.DirectorySeparatorChar))
            throw new InvalidOperationException("The destination folder is a subfolder of the source folder");

        try
        {
            if (srcObj.IsDir()) Directory.Move(srcPath, dstPath);
            else File.Move(srcPath, dstPath);
        }
        catch (IOException ex) when (IsCrossDeviceError(ex))
        {
            // Cross-device move: throw to indicate task-based move needed
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

        // Prevent copying to subfolder of itself
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

        // Recycle bin support
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
        using var fs = new FileStream(filePath, FileMode.Create);
        if (file.Stream != null)
        {
            await file.Stream.CopyToAsync(fs, 81920, ct);
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
                DiskUsage = new DiskUsage
                {
                    TotalSpace = driveInfo.TotalSize,
                    UsedSpace = driveInfo.TotalSize - driveInfo.AvailableFreeSpace
                }
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

    private static bool IsHidden(FileSystemInfo fsi)
    {
        if (fsi.Attributes.HasFlag(FileAttributes.Hidden)) return true;
        // Unix-style hidden files (start with .)
        return fsi.Name.StartsWith('.') && fsi.Name != "." && fsi.Name != "..";
    }

    private static bool IsCrossDeviceError(IOException ex)
    {
        // Cross-device link error on Unix (errno 18) or Windows ERROR_NOT_SAME_DEVICE
        return ex.HResult == 17 || // EXDEV on Linux
               ex.HResult == -2147024890 || // ERROR_NOT_SAME_DEVICE on Windows
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
    public bool Recursion { get; set; }
    public bool DirectorySize { get; set; }
    public bool Thumbnail { get; set; }
    public string? ThumbCacheFolder { get; set; }
    public string? ThumbConcurrency { get; set; }
    public string? VideoThumbPos { get; set; }
    public int MkdirPerm { get; set; }
    public string? RecycleBinPath { get; set; }
}
