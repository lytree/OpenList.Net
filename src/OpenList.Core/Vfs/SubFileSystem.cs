namespace OpenList.Vfs;

/// <summary>
/// 子文件系统视图（SubFileSystem）。
/// <para>
/// 把另一个文件系统中的某个子目录「提升」为当前文件系统的根：对外所有路径都被加上
/// <see cref="SubPath"/> 前缀后再转发给底层文件系统，对调用方而言它就是根。
/// 用于把本地磁盘的某个目录安全地暴露为一个挂载点，防止路径逃逸到该目录之外。
/// </para>
/// </summary>
public sealed class SubFileSystem : FileSystem
{
    private readonly IFileSystem _fs;
    private readonly UPath _subPath;

    /// <summary>构造：以 <paramref name="subPath"/> 为新的根。</summary>
    /// <param name="fileSystem">底层文件系统。</param>
    /// <param name="subPath">要暴露为根的子目录。</param>
    public SubFileSystem(IFileSystem fileSystem, UPath subPath)
    {
        _fs = fileSystem;
        _subPath = subPath;
    }

    /// <inheritdoc/>
    public override bool IsCaseSensitive => _fs.IsCaseSensitive;

    /// <summary>把对外路径翻译为底层文件系统的路径（加挂载前缀）。</summary>
    private UPath Translate(UPath path)
    {
        var rel = path.ToString();
        if (rel.Length <= 1) return _subPath;
        return _subPath == UPath.Root ? new UPath(rel) : _subPath / rel.TrimStart('/');
    }

    /// <inheritdoc/>
    protected override bool DirectoryExistsImpl(UPath path) => _fs.DirectoryExists(Translate(path));

    /// <inheritdoc/>
    protected override bool FileExistsImpl(UPath path) => _fs.FileExists(Translate(path));

    /// <inheritdoc/>
    protected override void CreateDirectoryImpl(UPath path) => _fs.CreateDirectory(Translate(path));

    /// <inheritdoc/>
    protected override void DeleteDirectoryImpl(UPath path, bool isRecursive)
        => _fs.DeleteDirectory(Translate(path), isRecursive);

    /// <inheritdoc/>
    protected override void DeleteFileImpl(UPath path) => _fs.DeleteFile(Translate(path));

    /// <inheritdoc/>
    protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
        => _fs.OpenFile(Translate(path), mode, access, share);

    /// <inheritdoc/>
    protected override void MoveFileImpl(UPath srcPath, UPath destPath)
        => _fs.MoveFile(Translate(srcPath), Translate(destPath));

    /// <inheritdoc/>
    protected override void MoveDirectoryImpl(UPath srcPath, UPath destPath)
        => _fs.MoveDirectory(Translate(srcPath), Translate(destPath));

    /// <inheritdoc/>
    protected override void CopyFileImpl(UPath srcPath, UPath destPath, bool overwrite)
        => _fs.CopyFile(Translate(srcPath), Translate(destPath), overwrite);

    /// <inheritdoc/>
    protected override void CopyDirectoryImpl(UPath srcPath, UPath destPath, bool overwrite)
        => _fs.CopyDirectory(Translate(srcPath), Translate(destPath), overwrite);

    /// <inheritdoc/>
    protected override IEnumerable<UPath> EnumeratePathsImpl(
        UPath path, string searchPattern, SearchDepth searchOption, SearchTarget searchTarget)
    {
        foreach (var child in _fs.EnumeratePaths(Translate(path), searchPattern, searchOption, searchTarget))
        {
            // 把底层返回的绝对路径反向裁掉挂载前缀，还原为对外的相对路径。
            yield return Strip(child);
        }
    }

    /// <summary>从底层绝对路径中裁掉挂载前缀。</summary>
    private UPath Strip(UPath absolute)
    {
        var full = absolute.ToString();
        var prefix = _subPath.ToString();
        if (prefix == "/") return new UPath(full);
        if (full.Length <= prefix.Length) return UPath.Root;
        return new UPath(full[prefix.Length..]);
    }

    /// <inheritdoc/>
    protected override long GetFileLengthImpl(UPath path) => _fs.GetFileLength(Translate(path));

    /// <inheritdoc/>
    protected override DateTime GetLastWriteTimeImpl(UPath path) => _fs.GetLastWriteTime(Translate(path));

    /// <inheritdoc/>
    protected override DateTime GetCreationTimeImpl(UPath path) => _fs.GetCreationTime(Translate(path));

    /// <inheritdoc/>
    protected override DateTime GetLastAccessTimeImpl(UPath path) => _fs.GetLastAccessTime(Translate(path));

    /// <inheritdoc/>
    protected override void SetLastWriteTimeImpl(UPath path, DateTime time)
        => _fs.SetLastWriteTime(Translate(path), time);

    /// <inheritdoc/>
    protected override void SetCreationTimeImpl(UPath path, DateTime time)
        => _fs.SetCreationTime(Translate(path), time);

    /// <inheritdoc/>
    protected override FileAttributes GetAttributesImpl(UPath path) => _fs.GetAttributes(Translate(path));

    /// <inheritdoc/>
    protected override void SetAttributesImpl(UPath path, FileAttributes attributes)
        => _fs.SetAttributes(Translate(path), attributes);

    /// <inheritdoc/>
    protected override string ConvertPathToInternalImpl(UPath path) => _fs.ConvertPathToInternal(Translate(path));

    /// <inheritdoc/>
    protected override UPath ConvertPathFromInternalImpl(string internalPath)
        => Strip(_fs.ConvertPathFromInternal(internalPath));

    /// <inheritdoc/>
    public override void Dispose()
    {
        // 不释放底层文件系统：其生命周期由创建方（驱动）管理。
        base.Dispose();
    }
}
