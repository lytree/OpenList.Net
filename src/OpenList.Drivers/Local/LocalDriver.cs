using System.Globalization;
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
    private DirectoryMap _directoryMap = new();
    private ITokenBucket _thumbTokenBucket = new NopTokenBucket();
    private double _videoThumbPos;
    private bool _videoThumbPosIsPercentage;

    public override DriverConfig Config() => new()
    {
        Name = "Local",
        LocalSort = true,
        OnlyProxy = true,
        NoCache = true,
        DefaultRoot = "/",
        NoLinkURL = true
    };

    public override object GetAddition() =>
        JsonSerializer.Deserialize<LocalAddition>(_storage.Addition) ?? new LocalAddition();

    protected override Type? GetAdditionType() => typeof(LocalAddition);

    public override Task InitAsync(CancellationToken ct = default)
    {
        var addition = (LocalAddition)GetAddition();
        RootFolderPath = addition.RootFolderPath;

        if (!Directory.Exists(RootFolderPath))
            throw new DirectoryNotFoundException($"Root folder {RootFolderPath} not exists");

        if (!Path.IsPathRooted(RootFolderPath))
            RootFolderPath = Path.GetFullPath(RootFolderPath);

        // Create FluentStorage blob storage backed by local directory
        _blobStorage = StorageFactory.Blobs.DirectoryFiles(RootFolderPath);

        // Thumb cache folder
        if (!string.IsNullOrEmpty(addition.ThumbCacheFolder) && !Directory.Exists(addition.ThumbCacheFolder))
            Directory.CreateDirectory(addition.ThumbCacheFolder);

        // Directory size calculation (Go: directoryMap)
        if (addition.DirectorySize)
        {
            _directoryMap.Root = RootFolderPath;
            _directoryMap.CalculateDirSize(RootFolderPath);
        }
        else
        {
            _directoryMap.Clear();
        }

        // Token bucket for thumbnail concurrency (Go: thumbConcurrency)
        var concurrency = 16;
        if (!string.IsNullOrEmpty(addition.ThumbConcurrency) &&
            int.TryParse(addition.ThumbConcurrency, out var c))
        {
            concurrency = c;
        }
        _thumbTokenBucket = concurrency > 0
            ? new StaticTokenBucket(concurrency)
            : new NopTokenBucket();

        // VideoThumbPos parsing (Go: VideoThumbPos)
        var videoThumbPos = addition.VideoThumbPos ?? "20%";
        if (videoThumbPos.EndsWith('%'))
        {
            var percentage = videoThumbPos.TrimEnd('%');
            if (!double.TryParse(percentage, NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
                throw new ArgumentException($"Invalid video_thumb_pos value: {videoThumbPos}");
            if (val < 0 || val > 100)
                throw new ArgumentException($"Invalid video_thumb_pos value: {videoThumbPos}, the percentage must be between 0 and 100");
            _videoThumbPosIsPercentage = true;
            _videoThumbPos = val / 100.0;
        }
        else
        {
            if (!double.TryParse(videoThumbPos, NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
                throw new ArgumentException($"Invalid video_thumb_pos value: {videoThumbPos}");
            if (val < 0)
                throw new ArgumentException($"Invalid video_thumb_pos value: {videoThumbPos}, the time must be a positive number");
            _videoThumbPosIsPercentage = false;
            _videoThumbPos = val;
        }

        return Task.CompletedTask;
    }

    public override Task DropAsync(CancellationToken ct = default)
    {
        _blobStorage?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// List directory contents using FluentStorage's ListAsync.
    /// Falls back to System.IO for hidden file filtering and directory size.
    /// </summary>
    public override async Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default)
    {
        var addition = (LocalAddition)GetAddition();
        var fullPath = dir.GetPath();

        // Refresh directory sizes if requested
        if (addition.DirectorySize && args.Refresh)
            _directoryMap.RecalculateDirSize();

        // Use FluentStorage to list entries
        var relativePath = GetRelativePath(fullPath);
        var blobs = await _blobStorage.ListAsync(new ListOptions { FolderPath = relativePath }, ct);

        var result = new List<IObj>();
        foreach (var blob in blobs)
        {
            if (blob.Name == "." || blob.Name == "..") continue;

            // Hidden file filter (requires System.IO check on Windows)
            if (!addition.ShowHidden && IsHiddenName(blob.Name)) continue;

            var entryPath = Path.Join(fullPath, blob.Name);
            var isFolder = blob.IsFolder;

            // Determine size
            long size = 0;
            if (isFolder)
            {
                if (addition.DirectorySize)
                {
                    var node = _directoryMap.Get(entryPath);
                    if (node != null)
                        size = node.FileSum + node.DirectorySum;
                }
            }
            else
            {
                size = blob.Size ?? 0;
            }

            // Get creation time from System.IO (FluentStorage doesn't expose ctime)
            var ctime = DateTime.MinValue;
            try { ctime = new FileInfo(entryPath).CreationTimeUtc; } catch { }

            result.Add(new ObjItem
            {
                Name = blob.Name,
                Size = size,
                Modified = blob.LastModificationTime?.UtcDateTime ?? DateTime.MinValue,
                CTime = ctime,
                IsFolder = isFolder,
                Path = entryPath
            });
        }

        return result;
    }

    /// <summary>
    /// Get a single object by path using FluentStorage's GetBlobAsync.
    /// </summary>
    public async Task<IObj?> GetAsync(string path, CancellationToken ct = default)
    {
        var addition = (LocalAddition)GetAddition();
        var actualPath = Path.Join(RootFolderPath, path.TrimStart('/'));

        var relativePath = GetRelativePath(actualPath);
        var blob = await _blobStorage.GetBlobAsync(relativePath, ct);
        if (blob == null)
        {
            // FluentStorage may not find directories in all providers; fall back to System.IO
            if (Directory.Exists(actualPath))
            {
                var di = new DirectoryInfo(actualPath);
                long size = 0;
                if (addition.DirectorySize)
                {
                    var node = _directoryMap.Get(actualPath);
                    if (node != null)
                        size = node.FileSum + node.DirectorySum;
                }
                return new ObjItem
                {
                    Name = di.Name,
                    Size = size,
                    Modified = di.LastWriteTimeUtc,
                    CTime = di.CreationTimeUtc,
                    IsFolder = true,
                    Path = actualPath
                };
            }
            return null;
        }

        var isFolder = blob.IsFolder || Directory.Exists(actualPath);
        long blobSize = blob.Size ?? 0;
        if (isFolder && addition.DirectorySize)
        {
            var node = _directoryMap.Get(actualPath);
            if (node != null)
                blobSize = node.FileSum + node.DirectorySum;
        }

        var ctime2 = DateTime.MinValue;
        try { ctime2 = new FileInfo(actualPath).CreationTimeUtc; } catch { }

        return new ObjItem
        {
            Name = blob.Name,
            Size = blobSize,
            Modified = blob.LastModificationTime?.UtcDateTime ?? DateTime.MinValue,
            CTime = ctime2,
            IsFolder = isFolder,
            Path = actualPath
        };
    }

    public override Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default)
    {
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
    /// Make directory. Uses Directory.CreateDirectory (FluentStorage has no mkdir API).
    /// </summary>
    public Task<IObj> MakeDirAsync(IObj parentDir, string dirName, CancellationToken ct = default)
    {
        var addition = (LocalAddition)GetAddition();
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

        // Update directory map
        if (addition.DirectorySize)
        {
            _directoryMap.Set(fullPath, new DirectoryNode { Children = [] });
            _directoryMap.UpdateDirSize(parentDir.GetPath());
            _directoryMap.UpdateDirParents(parentDir.GetPath());
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
    /// Move using FluentStorage's RenameAsync. Falls back to System.IO for directories.
    /// </summary>
    public async Task MoveAsync(IObj srcObj, IObj dstDir, CancellationToken ct = default)
    {
        var addition = (LocalAddition)GetAddition();
        var srcPath = srcObj.GetPath();
        var dstPath = Path.Join(dstDir.GetPath(), srcObj.GetName());

        if (dstPath.StartsWith(srcPath + Path.DirectorySeparatorChar))
            throw new InvalidOperationException("The destination folder is a subfolder of the source folder");

        try
        {
            if (srcObj.IsDir())
                Directory.Move(srcPath, dstPath);
            else
                File.Move(srcPath, dstPath);
        }
        catch (IOException ex) when (IsCrossDeviceError(ex))
        {
            throw new NotImplementedException("Cross-device move requires task-based processing");
        }

        // Update directory map
        if (addition.DirectorySize)
        {
            var srcParent = Path.GetDirectoryName(srcPath)!;
            var dstParent = Path.GetDirectoryName(dstPath)!;
            if (_directoryMap.Has(srcParent))
            {
                _directoryMap.UpdateDirSize(srcParent);
                _directoryMap.UpdateDirParents(srcParent);
            }
            if (_directoryMap.Has(dstParent))
            {
                _directoryMap.UpdateDirSize(dstParent);
                _directoryMap.UpdateDirParents(dstParent);
            }
        }
    }

    /// <summary>
    /// Rename using System.IO (FluentStorage.RenameAsync works but System.IO is simpler for local).
    /// </summary>
    public Task RenameAsync(IObj srcObj, string newName, CancellationToken ct = default)
    {
        var addition = (LocalAddition)GetAddition();
        var srcPath = srcObj.GetPath();
        var dir = Path.GetDirectoryName(srcPath)!;
        var dstPath = Path.Join(dir, newName);

        if (srcObj.IsDir()) Directory.Move(srcPath, dstPath);
        else File.Move(srcPath, dstPath);

        // Update directory map for renamed directories
        if (addition.DirectorySize && srcObj.IsDir())
        {
            if (_directoryMap.Has(srcPath))
            {
                _directoryMap.DeleteDirNode(srcPath);
                _directoryMap.CalculateDirSize(dstPath);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Copy using FluentStorage streams for files, recursive System.IO for directories.
    /// </summary>
    public async Task CopyAsync(IObj srcObj, IObj dstDir, CancellationToken ct = default)
    {
        var addition = (LocalAddition)GetAddition();
        var srcPath = srcObj.GetPath();
        var dstPath = Path.Join(dstDir.GetPath(), srcObj.GetName());

        if (dstPath.StartsWith(srcPath + Path.DirectorySeparatorChar))
            throw new InvalidOperationException("The destination folder is a subfolder of the source folder");

        if (srcObj.IsDir())
        {
            CopyDirectory(srcPath, dstPath);
        }
        else
        {
            // Use FluentStorage for file copy
            var srcRel = GetRelativePath(srcPath);
            var dstRel = GetRelativePath(dstPath);
            using var stream = await _blobStorage.OpenReadAsync(srcRel, ct);
            if (stream != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dstPath)!);
                await _blobStorage.WriteAsync(dstRel, stream, false, ct);
            }
        }

        // Update directory map
        if (addition.DirectorySize)
        {
            var dstParent = Path.GetDirectoryName(dstPath)!;
            if (_directoryMap.Has(dstParent))
            {
                _directoryMap.UpdateDirSize(dstParent);
                _directoryMap.UpdateDirParents(dstParent);
            }
        }
    }

    /// <summary>
    /// Remove using FluentStorage DeleteAsync or System.IO for recycle bin / directories.
    /// </summary>
    public async Task RemoveAsync(IObj obj, CancellationToken ct = default)
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

                // Update directory map after recycle
                UpdateDirectoryMapAfterRemove(addition, obj, path);
                return;
            }
            catch
            {
                // Fall through to permanent delete
            }
        }

        // Permanent delete using FluentStorage or System.IO
        if (obj.IsDir())
        {
            Directory.Delete(path, true);
        }
        else
        {
            var relativePath = GetRelativePath(path);
            await _blobStorage.DeleteAsync(relativePath, ct);
        }

        // Update directory map after remove
        UpdateDirectoryMapAfterRemove(addition, obj, path);
    }

    /// <summary>
    /// Put (upload) using FluentStorage WriteAsync. Cleans up on cancellation.
    /// </summary>
    public async Task<IObj> PutAsync(IObj dstDir, FileStreamer file, Action<double> updateProgress, CancellationToken ct = default)
    {
        var addition = (LocalAddition)GetAddition();
        var fullPath = Path.Join(dstDir.GetPath(), file.Obj.GetName());
        Directory.CreateDirectory(dstDir.GetPath());

        if (file.Stream != null)
        {
            var relativePath = GetRelativePath(fullPath);
            try
            {
                await _blobStorage.WriteAsync(relativePath, file.Stream, false, ct);
            }
            catch (OperationCanceledException)
            {
                // Clean up incomplete file on cancellation (Go: errors.Is(err, context.Canceled))
                try { if (File.Exists(fullPath)) File.Delete(fullPath); } catch { }
                throw;
            }
        }

        // Update directory map
        if (addition.DirectorySize)
        {
            _directoryMap.UpdateDirSize(dstDir.GetPath());
            _directoryMap.UpdateDirParents(dstDir.GetPath());
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

    // ── Helpers ──

    private void UpdateDirectoryMapAfterRemove(LocalAddition addition, IObj obj, string path)
    {
        if (!addition.DirectorySize) return;

        if (obj.IsDir())
        {
            if (_directoryMap.Has(path))
            {
                _directoryMap.DeleteDirNode(path);
                var parent = Path.GetDirectoryName(path);
                if (parent != null)
                {
                    _directoryMap.UpdateDirSize(parent);
                    _directoryMap.UpdateDirParents(parent);
                }
            }
        }
        else
        {
            var parent = Path.GetDirectoryName(path);
            if (parent != null && _directoryMap.Has(parent))
            {
                _directoryMap.UpdateDirSize(parent);
                _directoryMap.UpdateDirParents(parent);
            }
        }
    }

    /// <summary>
    /// Get FluentStorage relative path from absolute filesystem path.
    /// FluentStorage uses paths relative to the root directory.
    /// </summary>
    private string GetRelativePath(string absolutePath)
    {
        var rel = Path.GetRelativePath(RootFolderPath, absolutePath);
        if (rel == ".") return "";
        return rel.Replace(Path.DirectorySeparatorChar, '/');
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
        try { return Convert.ToInt32(perm, 8); } catch { return 0; }
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
