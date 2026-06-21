using System.Globalization;
using System.Text.Json;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Core.Models;
using Zio;
using Zio.FileSystems;

namespace OpenList.Drivers.Local;

public class LocalDriver : ZioDriverBase,
    IFileSystemLinkProvider, IFileSystemWithDetails, IFileSystemUploader
{
    private PhysicalFileSystem _physicalFs = null!;
    private SubFileSystem _subFs = null!;
    private DirectoryMap _directoryMap = new();
    private ITokenBucket _thumbTokenBucket = new NopTokenBucket();
    private double _videoThumbPos;
    private bool _videoThumbPosIsPercentage;
    private string _rootFolderPath = "/";

    public override DriverConfig Config { get; } = new()
    {
        Name = "Local",
        LocalSort = true,
        OnlyProxy = true,
        NoCache = true,
        DefaultRoot = "/",
        NoLinkURL = true
    };

    public string RootFolderPath => _rootFolderPath;

    private LocalAddition? _addition;
    public override object Addition => _addition ??= JsonSerializer.Deserialize<LocalAddition>(_storage?.Addition ?? "{}") ?? new LocalAddition();

    public override DriverInfo GetDriverInfo()
    {
        var cfg = Config;
        var addition = new LocalAddition();
        var additionalItems = BuildItemsFromObject(addition);

        var commonItems = new List<DriverItem>
        {
            new() { Name = "mount_path", Type = "string", Required = true, Help = "The path you want to mount to, it is unique and cannot be repeated" },
            new() { Name = "order", Type = "number", Help = "use to sort" },
            new() { Name = "remark", Type = "text" },
        };

        // OnlyProxy settings
        commonItems.Add(new DriverItem { Name = "webdav_policy", Type = "select", Options = "use_proxy_url,native_proxy", Default = "native_proxy", Required = true });
        commonItems.Add(new DriverItem { Name = "down_proxy_url", Type = "text" });
        commonItems.Add(new DriverItem { Name = "disable_proxy_sign", Type = "bool", Default = "false", Help = "Disable sign for Download proxy URL" });

        // LocalSort settings
        commonItems.Add(new DriverItem { Name = "order_by", Type = "select", Options = "name,size,modified" });
        commonItems.Add(new DriverItem { Name = "order_direction", Type = "select", Options = "asc,desc" });

        commonItems.Add(new DriverItem { Name = "extract_folder", Type = "select", Options = "front,back" });
        commonItems.Add(new DriverItem { Name = "disable_index", Type = "bool", Default = "false", Required = true });
        commonItems.Add(new DriverItem { Name = "enable_sign", Type = "bool", Default = "false", Required = true });

        return new DriverInfo
        {
            Common = commonItems,
            Additional = additionalItems,
            Config = cfg
        };
    }

    public override Task InitAsync(CancellationToken ct = default)
    {
        var addition = (LocalAddition)Addition;
        _rootFolderPath = addition.RootFolderPath;

        if (!Directory.Exists(_rootFolderPath))
            throw new DirectoryNotFoundException($"Root folder {_rootFolderPath} not exists");

        if (!Path.IsPathRooted(_rootFolderPath))
            _rootFolderPath = Path.GetFullPath(_rootFolderPath);

        // Create Zio PhysicalFileSystem and SubFileSystem scoped to root
        _physicalFs = new PhysicalFileSystem();
        var rootUPath = _physicalFs.ConvertPathFromInternal(_rootFolderPath);
        _subFs = new SubFileSystem(_physicalFs, rootUPath, owned: false);

        // Thumb cache folder
        if (!string.IsNullOrEmpty(addition.ThumbCacheFolder) && !Directory.Exists(addition.ThumbCacheFolder))
            Directory.CreateDirectory(addition.ThumbCacheFolder);

        // Directory size calculation
        if (addition.DirectorySize)
        {
            _directoryMap.Root = _rootFolderPath;
            _directoryMap.CalculateDirSize(_rootFolderPath);
        }
        else
        {
            _directoryMap.Clear();
        }

        // Token bucket for thumbnail concurrency
        var concurrency = 16;
        if (!string.IsNullOrEmpty(addition.ThumbConcurrency) &&
            int.TryParse(addition.ThumbConcurrency, out var c))
        {
            concurrency = c;
        }
        _thumbTokenBucket = concurrency > 0
            ? new StaticTokenBucket(concurrency)
            : new NopTokenBucket();

        // VideoThumbPos parsing
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
        _subFs?.Dispose();
        _physicalFs?.Dispose();
        _subFs = null!;
        _physicalFs = null!;
        return Task.CompletedTask;
    }

    #region IFileSystem Impl overrides (delegate to SubFileSystem)

    protected override void CreateDirectoryImpl(UPath path)
    {
        var addition = (LocalAddition)Addition;
        _subFs.CreateDirectory(path);

        // Apply permissions
        var perm = ParseOctalPermission(addition.MkdirPerm);
        if (perm > 0)
        {
            try
            {
                var internalPath = GetInternalPath(path);
                var dirInfo = new DirectoryInfo(internalPath);
                dirInfo.Attributes = (FileAttributes)perm;
            }
            catch { }
        }

        // Update directory map
        if (addition.DirectorySize)
        {
            var internalPath = GetInternalPath(path);
            _directoryMap.Set(internalPath, new DirectoryNode { Children = [] });
            var parentPath = path.GetDirectory();
            if (!parentPath.IsNull)
            {
                var parentInternal = GetInternalPath(parentPath);
                _directoryMap.UpdateDirSize(parentInternal);
                _directoryMap.UpdateDirParents(parentInternal);
            }
        }
    }

    protected override bool DirectoryExistsImpl(UPath path) => _subFs.DirectoryExists(path);

    protected override void MoveDirectoryImpl(UPath srcPath, UPath destPath)
    {
        var addition = (LocalAddition)Addition;
        _subFs.MoveDirectory(srcPath, destPath);

        if (addition.DirectorySize)
        {
            var srcInternal = GetInternalPath(srcPath);
            var dstInternal = GetInternalPath(destPath);
            if (_directoryMap.Has(srcInternal))
            {
                _directoryMap.DeleteDirNode(srcInternal);
                _directoryMap.CalculateDirSize(dstInternal);
            }
            var srcParent = Path.GetDirectoryName(srcInternal);
            var dstParent = Path.GetDirectoryName(dstInternal);
            if (srcParent != null && _directoryMap.Has(srcParent))
            {
                _directoryMap.UpdateDirSize(srcParent);
                _directoryMap.UpdateDirParents(srcParent);
            }
            if (dstParent != null && _directoryMap.Has(dstParent))
            {
                _directoryMap.UpdateDirSize(dstParent);
                _directoryMap.UpdateDirParents(dstParent);
            }
        }
    }

    protected override void DeleteDirectoryImpl(UPath path, bool isRecursive)
    {
        var addition = (LocalAddition)Addition;
        var internalPath = GetInternalPath(path);

        // Recycle bin support
        if (!string.IsNullOrEmpty(addition.RecycleBinPath) &&
            addition.RecycleBinPath != "delete permanently" && !isRecursive)
        {
            try
            {
                var relPath = Path.GetRelativePath(_rootFolderPath, Path.GetDirectoryName(internalPath)!);
                var recycleDir = Path.Join(addition.RecycleBinPath, relPath);
                if (!Directory.Exists(recycleDir))
                    Directory.CreateDirectory(recycleDir);

                var name = path.GetName();
                var dstPath = Path.Join(recycleDir, name);
                if (File.Exists(dstPath) || Directory.Exists(dstPath))
                    dstPath = Path.Join(recycleDir, $"{name}_{DateTime.Now:yyyyMMddHHmmss}");

                Directory.Move(internalPath, dstPath);
                UpdateDirectoryMapAfterRemove(addition, internalPath, isDir: true);
                return;
            }
            catch { /* Fall through to permanent delete */ }
        }

        _subFs.DeleteDirectory(path, isRecursive);
        UpdateDirectoryMapAfterRemove(addition, internalPath, isDir: true);
    }

    protected override void CopyFileImpl(UPath srcPath, UPath destPath, bool overwrite) =>
        _subFs.CopyFile(srcPath, destPath, overwrite);

    protected override void ReplaceFileImpl(UPath srcPath, UPath destPath, UPath destBackupPath, bool ignoreMetadataErrors) =>
        _subFs.ReplaceFile(srcPath, destPath, destBackupPath, ignoreMetadataErrors);

    protected override long GetFileLengthImpl(UPath path) => _subFs.GetFileLength(path);

    protected override bool FileExistsImpl(UPath path) => _subFs.FileExists(path);

    protected override void MoveFileImpl(UPath srcPath, UPath destPath)
    {
        var addition = (LocalAddition)Addition;
        _subFs.MoveFile(srcPath, destPath);

        if (addition.DirectorySize)
        {
            var srcInternal = GetInternalPath(srcPath);
            var dstInternal = GetInternalPath(destPath);
            var srcParent = Path.GetDirectoryName(srcInternal);
            var dstParent = Path.GetDirectoryName(dstInternal);
            if (srcParent != null && _directoryMap.Has(srcParent))
            {
                _directoryMap.UpdateDirSize(srcParent);
                _directoryMap.UpdateDirParents(srcParent);
            }
            if (dstParent != null && _directoryMap.Has(dstParent))
            {
                _directoryMap.UpdateDirSize(dstParent);
                _directoryMap.UpdateDirParents(dstParent);
            }
        }
    }

    protected override void DeleteFileImpl(UPath path)
    {
        var addition = (LocalAddition)Addition;
        var internalPath = GetInternalPath(path);

        // Recycle bin support
        if (!string.IsNullOrEmpty(addition.RecycleBinPath) &&
            addition.RecycleBinPath != "delete permanently")
        {
            try
            {
                var relPath = Path.GetRelativePath(_rootFolderPath, Path.GetDirectoryName(internalPath)!);
                var recycleDir = Path.Join(addition.RecycleBinPath, relPath);
                if (!Directory.Exists(recycleDir))
                    Directory.CreateDirectory(recycleDir);

                var name = path.GetName();
                var dstPath = Path.Join(recycleDir, name);
                if (File.Exists(dstPath) || Directory.Exists(dstPath))
                    dstPath = Path.Join(recycleDir, $"{name}_{DateTime.Now:yyyyMMddHHmmss}");

                File.Move(internalPath, dstPath);
                UpdateDirectoryMapAfterRemove(addition, internalPath, isDir: false);
                return;
            }
            catch { /* Fall through to permanent delete */ }
        }

        _subFs.DeleteFile(path);
        UpdateDirectoryMapAfterRemove(addition, internalPath, isDir: false);
    }

    protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share) =>
        _subFs.OpenFile(path, mode, access, share);

    protected override FileAttributes GetAttributesImpl(UPath path) => _subFs.GetAttributes(path);
    protected override void SetAttributesImpl(UPath path, FileAttributes attributes) => _subFs.SetAttributes(path, attributes);
    protected override DateTime GetCreationTimeImpl(UPath path) => _subFs.GetCreationTime(path);
    protected override void SetCreationTimeImpl(UPath path, DateTime time) => _subFs.SetCreationTime(path, time);
    protected override DateTime GetLastAccessTimeImpl(UPath path) => _subFs.GetLastAccessTime(path);
    protected override void SetLastAccessTimeImpl(UPath path, DateTime time) => _subFs.SetLastAccessTime(path, time);
    protected override DateTime GetLastWriteTimeImpl(UPath path) => _subFs.GetLastWriteTime(path);
    protected override void SetLastWriteTimeImpl(UPath path, DateTime time) => _subFs.SetLastWriteTime(path, time);

    protected override IEnumerable<UPath> EnumeratePathsImpl(UPath path, string searchPattern, SearchOption searchOption, SearchTarget searchTarget) =>
        _subFs.EnumeratePaths(path, searchPattern, searchOption, searchTarget);

    protected override IEnumerable<FileSystemItem> EnumerateItemsImpl(UPath path, SearchOption searchOption, SearchPredicate? searchPredicate) =>
        _subFs.EnumerateItems(path, searchOption, searchPredicate);

    protected override string ConvertPathToInternalImpl(UPath path) =>
        _subFs.ConvertPathToInternal(path);

    protected override UPath ConvertPathFromInternalImpl(string systemPath) =>
        _subFs.ConvertPathFromInternal(systemPath);

    protected override (IFileSystem FileSystem, UPath Path) ResolvePathImpl(UPath path) =>
        _subFs.ResolvePath(path);

    #endregion

    #region IFileSystemLinkProvider

    public Task<Link?> GetLinkAsync(UPath path, LinkArgs args, CancellationToken ct = default)
    {
        var internalPath = GetInternalPath(path);
        if (!File.Exists(internalPath)) return Task.FromResult<Link?>(null);

        var link = new Link { URL = internalPath };

        if (args.Type == "thumb")
        {
            var addition = (LocalAddition)Addition;
            if (addition.Thumbnail)
            {
                var thumbPath = GetThumbnailPath(path, addition);
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

    #endregion

    #region IFileSystemUploader

    public async Task<UPath> UploadAsync(UPath dstDir, Stream stream, string fileName, Action<double> progress, CancellationToken ct = default)
    {
        var addition = (LocalAddition)Addition;
        var filePath = dstDir / fileName;

        _subFs.CreateDirectory(dstDir);

        try
        {
            using var fs = _subFs.OpenFile(filePath, FileMode.Create, FileAccess.Write);
            var buffer = new byte[81920];
            long totalRead = 0;
            var totalSize = stream.CanSeek ? stream.Length : -1;
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalRead += bytesRead;
                if (totalSize > 0)
                    progress(totalRead / (double)totalSize);
            }
        }
        catch (OperationCanceledException)
        {
            try { if (_subFs.FileExists(filePath)) _subFs.DeleteFile(filePath); } catch { }
            throw;
        }

        // Update directory map
        if (addition.DirectorySize)
        {
            var internalDir = GetInternalPath(dstDir);
            _directoryMap.UpdateDirSize(internalDir);
            _directoryMap.UpdateDirParents(internalDir);
        }

        progress(1.0);
        return filePath;
    }

    #endregion

    #region IFileSystemWithDetails

    public Task<StorageDetails?> GetDetailsAsync(CancellationToken ct = default)
    {
        try
        {
            var root = Path.GetPathRoot(_rootFolderPath);
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

    #endregion

    #region Helpers

    private string GetInternalPath(UPath path) => _subFs.ConvertPathToInternal(path);

    private void UpdateDirectoryMapAfterRemove(LocalAddition addition, string internalPath, bool isDir)
    {
        if (!addition.DirectorySize) return;

        if (isDir)
        {
            if (_directoryMap.Has(internalPath))
            {
                _directoryMap.DeleteDirNode(internalPath);
                var parent = Path.GetDirectoryName(internalPath);
                if (parent != null)
                {
                    _directoryMap.UpdateDirSize(parent);
                    _directoryMap.UpdateDirParents(parent);
                }
            }
        }
        else
        {
            var parent = Path.GetDirectoryName(internalPath);
            if (parent != null && _directoryMap.Has(parent))
            {
                _directoryMap.UpdateDirSize(parent);
                _directoryMap.UpdateDirParents(parent);
            }
        }
    }

    private static bool IsHiddenName(string name) =>
        name.StartsWith('.') && name != "." && name != "..";

    private static int ParseOctalPermission(string? perm)
    {
        if (string.IsNullOrEmpty(perm)) return 0;
        try { return Convert.ToInt32(perm, 8); } catch { return 0; }
    }

    private string? GetThumbnailPath(UPath file, LocalAddition addition)
    {
        if (string.IsNullOrEmpty(addition.ThumbCacheFolder)) return null;
        var name = file.GetName();
        var ext = Path.GetExtension(name).ToLowerInvariant();
        var isImage = ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp";
        var isVideo = ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".flv" or ".webm";
        if (!isImage && !isVideo) return null;

        var hash = name.GetHashCode().ToString("x");
        return Path.Join(addition.ThumbCacheFolder, $"{hash}.png");
    }

    #endregion
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
