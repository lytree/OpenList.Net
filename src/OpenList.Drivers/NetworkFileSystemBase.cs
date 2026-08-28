using Zio;
using Zio.FileSystems;

namespace OpenList.Drivers;

/// <summary>
/// 基于网络协议（S3、WebDAV、FTP、SFTP、SMB）的 Zio 文件系统基类。
/// 网络协议通常不支持符号链接、文件属性与文件监视，本类为其提供默认实现；
/// 子类只需实现协议相关的核心操作。
/// </summary>
internal abstract class NetworkFileSystemBase : FileSystem
{
    // --- 路径转换（Zio 中的抽象方法） ---

    /// <summary>把 Zio 路径转换为协议内部使用的绝对路径字符串。</summary>
    protected override string ConvertPathToInternalImpl(UPath path) => path.FullName;
    /// <summary>把协议内部路径字符串转换回 Zio 路径。</summary>
    protected override UPath ConvertPathFromInternalImpl(string internalPath) => new UPath(internalPath);

    // --- 网络协议不支持的操作为默认空实现/抛异常 ---

    /// <summary>网络文件系统不支持符号链接，抛出 <see cref="NotSupportedException"/>。</summary>
    protected override void CreateSymbolicLinkImpl(UPath path, UPath pathToTarget)
        => throw new NotSupportedException("Symbolic links are not supported on network filesystems.");

    /// <summary>网络文件系统无符号链接可解析，始终返回 false。</summary>
    protected override bool TryResolveLinkTargetImpl(UPath link, out UPath resolvedPath)
    {
        resolvedPath = default;
        return false;
    }

    /// <summary>
    /// 依据文件/目录的存在性推断文件属性：目录返回 Directory，文件返回 Normal。
    /// </summary>
    protected override FileAttributes GetAttributesImpl(UPath path)
    {
        if (DirectoryExists(path)) return FileAttributes.Directory;
        if (FileExists(path)) return FileAttributes.Normal;
        throw new FileNotFoundException($"Path not found: {path}");
    }

    /// <summary>网络协议通常不支持设置文件属性，空实现忽略请求。</summary>
    protected override void SetAttributesImpl(UPath path, FileAttributes attributes)
    {
        // 网络协议通常不支持设置文件属性。
    }

    /// <summary>网络协议通常不支持设置写入时间，空实现忽略请求。</summary>
    protected override void SetLastWriteTimeImpl(UPath path, DateTime time)
    {
    }

    /// <summary>网络协议通常不支持设置创建时间，空实现忽略请求。</summary>
    protected override void SetCreationTimeImpl(UPath path, DateTime time)
    {
    }

    /// <summary>网络文件系统不支持文件监视，返回 null。</summary>
    protected override IFileSystemWatcher? WatchImpl(UPath path)
    {
        // 网络文件系统不支持文件系统监视。
        return null;
    }

    // --- 基于其它原语的默认实现 ---

    /// <summary>默认通过打开文件读取流并取其长度。</summary>
    protected override long GetFileLengthImpl(UPath path)
    {
        using var stream = OpenFile(path, FileMode.Open, FileAccess.Read);
        return stream.Length;
    }

    /// <summary>网络协议通常没有单独的访问时间，退化为写入时间。</summary>
    protected override DateTime GetLastAccessTimeImpl(UPath path) => GetLastWriteTime(path);

    /// <summary>网络协议通常不支持设置访问时间，空实现忽略请求。</summary>
    protected override void SetLastAccessTimeImpl(UPath path, DateTime time)
    {
    }

    /// <summary>
    /// 默认 ReplaceFile：备份目标（可选）→ 删除目标 → 将源移动到目标。
    /// </summary>
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
    /// 默认 MoveDirectory：先复制后删除源目录。
    /// 子类可覆盖为协议原生的移动实现。
    /// </summary>
    protected override void MoveDirectoryImpl(UPath srcPath, UPath destPath)
    {
        CopyDirectory(srcPath, destPath);
        DeleteDirectory(srcPath, isRecursive: true);
    }

    /// <summary>
    /// 默认 CopyDirectory：逐层创建目标目录并递归复制子项。
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

    /// <summary>
    /// 默认 CopyFile：若不允许覆盖且目标已存在则报错，否则流式复制内容。
    /// </summary>
    protected override void CopyFileImpl(UPath srcPath, UPath destPath, bool overwrite)
    {
        if (!overwrite && FileExists(destPath))
            throw new IOException($"File already exists: {destPath}");
        using var src = OpenFile(srcPath, FileMode.Open, FileAccess.Read);
        using var dst = OpenFile(destPath, FileMode.Create, FileAccess.Write);
        src.CopyTo(dst);
    }

    /// <summary>
    /// 默认 EnumerateItems：复用 EnumeratePaths 枚举并组装 <see cref="FileSystemItem"/>。
    /// 子类可覆盖以提升效率。
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
            // 仅当无谓词或谓词命中时产出该项。
            if (predicate == null || predicate(ref item))
                yield return item;
        }
    }
}