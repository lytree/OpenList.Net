using Zio;
using Zio.FileSystems;

namespace OpenList.Drivers;

/// <summary>
/// Base class for network-backed Zio filesystems (S3, WebDAV, FTP, SFTP, SMB).
/// Provides default implementations for operations that network protocols typically
/// don't support (symlinks, attributes, file watching). Subclasses implement the
/// protocol-specific operations.
/// </summary>
internal abstract class NetworkFileSystemBase : FileSystem
{
    // --- Path conversion (abstract in Zio) ---

    protected override string ConvertPathToInternalImpl(UPath path) => path.FullName;
    protected override UPath ConvertPathFromInternalImpl(string internalPath) => new UPath(internalPath);

    // --- Operations not supported by network protocols ---

    protected override void CreateSymbolicLinkImpl(UPath path, UPath pathToTarget)
        => throw new NotSupportedException("Symbolic links are not supported on network filesystems.");

    protected override bool TryResolveLinkTargetImpl(UPath link, out UPath resolvedPath)
    {
        resolvedPath = default;
        return false;
    }

    protected override FileAttributes GetAttributesImpl(UPath path)
    {
        if (DirectoryExists(path)) return FileAttributes.Directory;
        if (FileExists(path)) return FileAttributes.Normal;
        throw new FileNotFoundException($"Path not found: {path}");
    }

    protected override void SetAttributesImpl(UPath path, FileAttributes attributes)
    {
        // Network protocols typically don't support setting file attributes.
    }

    protected override void SetLastWriteTimeImpl(UPath path, DateTime time)
    {
    }

    protected override void SetCreationTimeImpl(UPath path, DateTime time)
    {
    }

    protected override IFileSystemWatcher? WatchImpl(UPath path)
    {
        // Network filesystems don't support file system watchers.
        return null;
    }

    // --- Default implementations using other primitives ---

    protected override long GetFileLengthImpl(UPath path)
    {
        using var stream = OpenFile(path, FileMode.Open, FileAccess.Read);
        return stream.Length;
    }

    protected override DateTime GetLastAccessTimeImpl(UPath path) => GetLastWriteTime(path);

    protected override void SetLastAccessTimeImpl(UPath path, DateTime time)
    {
    }

    protected override void ReplaceFileImpl(UPath sourcePath, UPath destinationPath, UPath destinationBackupPath, bool ignoreMetadataErrors)
    {
        if (destinationBackupPath != default)
        {
            CopyFile(destinationPath, destinationBackupPath, overwrite: true);
        }
        DeleteFile(destinationPath);
        MoveFile(sourcePath, destinationPath);
    }

    /// <summary>
    /// Default MoveDirectory: copy then delete source.
    /// Subclasses may override for protocol-native move.
    /// </summary>
    protected override void MoveDirectoryImpl(UPath srcPath, UPath destPath)
    {
        CopyDirectory(srcPath, destPath);
        DeleteDirectory(srcPath, isRecursive: true);
    }

    /// <summary>
    /// Default CopyDirectory implementation.
    /// </summary>
    protected virtual void CopyDirectory(UPath srcPath, UPath destPath)
    {
        CreateDirectory(destPath);
        foreach (var p in EnumeratePaths(srcPath, "*", SearchOption.TopDirectoryOnly, SearchTarget.Both))
        {
            var dest = destPath / p.GetName();
            if (DirectoryExists(p))
            {
                CopyDirectory(p, dest);
            }
            else
            {
                CopyFile(p, dest, overwrite: true);
            }
        }
    }

    protected override void CopyFileImpl(UPath srcPath, UPath destPath, bool overwrite)
    {
        if (!overwrite && FileExists(destPath))
            throw new IOException($"File already exists: {destPath}");
        using var src = OpenFile(srcPath, FileMode.Open, FileAccess.Read);
        using var dst = OpenFile(destPath, FileMode.Create, FileAccess.Write);
        src.CopyTo(dst);
    }

    /// <summary>
    /// Default EnumerateItems implementation using EnumeratePaths.
    /// Subclasses may override for efficiency.
    /// </summary>
    protected override IEnumerable<FileSystemItem> EnumerateItemsImpl(UPath path, SearchOption searchOption, SearchPredicate? predicate)
    {
        foreach (var p in EnumeratePaths(path, "*", searchOption, SearchTarget.Both))
        {
            var isDir = DirectoryExists(p);
            var item = new FileSystemItem
            {
                FileSystem = this,
                Path = p,
                Attributes = isDir ? FileAttributes.Directory : FileAttributes.Normal,
                Length = isDir ? 0 : GetFileLength(p),
                LastWriteTime = GetLastWriteTime(p),
                CreationTime = GetCreationTime(p),
            };
            if (predicate == null || predicate(ref item))
                yield return item;
        }
    }
}
