namespace OpenList.Vfs;

/// <summary>
/// 文件系统抽象基类（FileSystem）。
/// <para>
/// 公共方法负责路径校验、存在性检查与前置条件判断，再委派给 <c>*Impl</c> 虚方法；
/// 子类只需覆写协议相关的核心操作即可。基于其它原语的组合逻辑
/// （复制目录、枚举条目、替换文件等）在此提供默认实现，网络类驱动可复用。
/// </para>
/// </summary>
public abstract class FileSystem : IFileSystem
{
    /// <inheritdoc/>
    public char DirectorySeparator => '/';

    /// <inheritdoc/>
    public virtual bool IsCaseSensitive => true;

    /// <summary>校验路径是否可用；<paramref name="mustExist"/> 为 true 时同时要求路径存在。</summary>
    protected virtual void ValidatePath(UPath path, bool mustExist)
    {
        if (path.IsNull)
            throw new ArgumentNullException(nameof(path), "path must not be null");
        // 根路径恒视为存在：它是每个文件系统的固有入口，
        // 否则挂载点根（未挂在任何子文件系统下）无法被枚举。
        if (mustExist && path != UPath.Root)
        {
            if (!DirectoryExists(path) && !FileExists(path))
                throw new FileNotFoundException($"Path not found: {path}");
        }
    }

    // --- 抽象核心：存在性 ---

    /// <summary>目录是否存在的实现。</summary>
    protected abstract bool DirectoryExistsImpl(UPath path);

    /// <summary>文件是否存在的实现。</summary>
    protected abstract bool FileExistsImpl(UPath path);

    // --- 抽象核心：目录 ---

    /// <summary>创建目录的实现。</summary>
    protected abstract void CreateDirectoryImpl(UPath path);

    /// <summary>删除目录的实现。</summary>
    protected abstract void DeleteDirectoryImpl(UPath path, bool isRecursive);

    // --- 抽象核心：文件 ---

    /// <summary>删除文件的实现。</summary>
    protected abstract void DeleteFileImpl(UPath path);

    /// <summary>打开文件的实现。</summary>
    protected abstract Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share);

    // --- 抽象核心：枚举 ---

    /// <summary>枚举子路径的实现。</summary>
    protected abstract IEnumerable<UPath> EnumeratePathsImpl(
        UPath path, string searchPattern, SearchDepth searchOption, SearchTarget searchTarget);

    // --- 默认实现：移动 / 复制 ---

    /// <summary>移动文件的默认实现：流式复制后删除源。</summary>
    protected virtual void MoveFileImpl(UPath srcPath, UPath destPath)
    {
        CopyFile(srcPath, destPath, overwrite: false);
        DeleteFile(srcPath);
    }

    /// <summary>移动目录的默认实现：先复制后删除源目录。</summary>
    protected virtual void MoveDirectoryImpl(UPath srcPath, UPath destPath)
    {
        CopyDirectory(srcPath, destPath, overwrite: false);
        DeleteDirectory(srcPath, isRecursive: true);
    }

    /// <summary>复制文件的默认实现：若不允许覆盖且目标已存在则报错，否则流式复制内容。</summary>
    protected virtual void CopyFileImpl(UPath srcPath, UPath destPath, bool overwrite)
    {
        if (!overwrite && FileExists(destPath))
            throw new IOException($"File already exists: {destPath}");
        using var src = OpenFile(srcPath, FileMode.Open, FileAccess.Read);
        using var dst = OpenFile(destPath, FileMode.Create, FileAccess.Write);
        src.CopyTo(dst);
    }

    /// <summary>复制目录的默认实现：逐层创建目标目录并递归复制子项。</summary>
    protected virtual void CopyDirectoryImpl(UPath srcPath, UPath destPath, bool overwrite)
    {
        if (!DirectoryExists(srcPath))
            throw new DirectoryNotFoundException($"Source directory not found: {srcPath}");

        if (!DirectoryExists(destPath))
        {
            CreateDirectory(destPath);
        }
        else if (!overwrite)
        {
            // 目标已存在且不允许覆盖：逐项判断冲突，任一冲突即报错。
            foreach (var p in EnumeratePaths(srcPath))
            {
                var dest = destPath / p.GetName();
                if (FileExists(dest) || DirectoryExists(dest))
                    throw new IOException($"Destination already exists: {dest}");
            }
        }

        foreach (var p in EnumeratePaths(srcPath, "*", SearchDepth.TopDirectoryOnly, SearchTarget.Both))
        {
            var dest = destPath / p.GetName();
            if (DirectoryExists(p))
                CopyDirectory(p, dest, overwrite);
            else
                CopyFile(p, dest, overwrite);
        }
    }

    // --- 默认实现：元数据 ---

    /// <summary>获取文件长度的默认实现：打开流读取其长度。</summary>
    protected virtual long GetFileLengthImpl(UPath path)
    {
        using var stream = OpenFile(path, FileMode.Open, FileAccess.Read);
        return stream.Length;
    }

    /// <summary>获取最后写入时间的实现（抽象）。</summary>
    protected abstract DateTime GetLastWriteTimeImpl(UPath path);

    /// <summary>获取创建时间的默认实现：退化为最后写入时间。</summary>
    protected virtual DateTime GetCreationTimeImpl(UPath path) => GetLastWriteTimeImpl(path);

    /// <summary>获取最后访问时间的默认实现：退化为最后写入时间。</summary>
    protected virtual DateTime GetLastAccessTimeImpl(UPath path) => GetLastWriteTimeImpl(path);

    /// <summary>设置最后写入时间的实现（抽象）。</summary>
    protected abstract void SetLastWriteTimeImpl(UPath path, DateTime time);

    /// <summary>设置创建时间的默认实现：忽略请求。</summary>
    protected virtual void SetCreationTimeImpl(UPath path, DateTime time)
    {
    }

    /// <summary>设置最后访问时间的默认实现：忽略请求。</summary>
    protected virtual void SetLastAccessTimeImpl(UPath path, DateTime time)
    {
    }

    /// <summary>获取文件属性的默认实现：按存在性推断。</summary>
    protected virtual FileAttributes GetAttributesImpl(UPath path)
    {
        if (DirectoryExists(path)) return FileAttributes.Directory;
        if (FileExists(path)) return FileAttributes.Normal;
        throw new FileNotFoundException($"Path not found: {path}");
    }

    /// <summary>设置文件属性的默认实现：忽略请求。</summary>
    protected virtual void SetAttributesImpl(UPath path, FileAttributes attributes)
    {
    }

    // --- 默认实现：枚举条目 ---

    /// <summary>
    /// 枚举条目的默认实现：复用 <see cref="EnumeratePathsImpl"/> 逐项查询元数据。
    /// 子类可覆写为「一次远端请求返回完整元数据」以避免 N+1。
    /// </summary>
    protected virtual IEnumerable<FileSystemItem> EnumerateItemsImpl(
        UPath path, SearchDepth searchOption, SearchPredicate? predicate)
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
                LastAccessTime = GetLastAccessTime(p),
            };
            if (predicate is null || predicate(ref item))
                yield return item;
        }
    }

    // --- 默认实现：路径转换 ---

    /// <summary>把内部路径转换为文件系统路径的默认实现：视为已是本文件系统路径。</summary>
    protected virtual UPath ConvertPathFromInternalImpl(string internalPath) => new(internalPath);

    /// <summary>把文件系统路径转换为内部路径的默认实现：取规范化全名。</summary>
    protected virtual string ConvertPathToInternalImpl(UPath path) => path.ToString();

    // --- 公共 API：委派到虚方法 ---

    /// <inheritdoc/>
    public bool DirectoryExists(UPath path)
    {
        if (path.IsNull) return false;
        // 根路径恒存在。
        if (path == UPath.Root) return true;
        return DirectoryExistsImpl(path);
    }

    /// <inheritdoc/>
    public bool FileExists(UPath path)
    {
        if (path.IsNull) return false;
        return FileExistsImpl(path);
    }

    /// <inheritdoc/>
    public void CreateDirectory(UPath path)
    {
        ValidatePath(path, mustExist: false);
        if (DirectoryExists(path))
            throw new IOException($"Directory already exists: {path}");
        CreateDirectoryImpl(path);
    }

    /// <inheritdoc/>
    public void DeleteDirectory(UPath path, bool isRecursive = false)
    {
        ValidatePath(path, mustExist: true);
        DeleteDirectoryImpl(path, isRecursive);
    }

    /// <inheritdoc/>
    public void DeleteFile(UPath path)
    {
        ValidatePath(path, mustExist: true);
        DeleteFileImpl(path);
    }

    /// <inheritdoc/>
    public void MoveFile(UPath srcPath, UPath destPath)
    {
        ValidatePath(srcPath, mustExist: true);
        ValidatePath(destPath, mustExist: false);
        MoveFileImpl(srcPath, destPath);
    }

    /// <inheritdoc/>
    public void MoveDirectory(UPath srcPath, UPath destPath)
    {
        ValidatePath(srcPath, mustExist: true);
        ValidatePath(destPath, mustExist: false);
        MoveDirectoryImpl(srcPath, destPath);
    }

    /// <inheritdoc/>
    public void CopyFile(UPath srcPath, UPath destPath, bool overwrite = false)
    {
        ValidatePath(srcPath, mustExist: true);
        ValidatePath(destPath, mustExist: false);
        CopyFileImpl(srcPath, destPath, overwrite);
    }

    /// <inheritdoc/>
    public void CopyDirectory(UPath srcPath, UPath destPath, bool overwrite = false)
    {
        ValidatePath(srcPath, mustExist: true);
        ValidatePath(destPath, mustExist: false);
        CopyDirectoryImpl(srcPath, destPath, overwrite);
    }

    /// <inheritdoc/>
    public Stream OpenFile(UPath path, FileMode mode, FileAccess access, FileShare share = FileShare.None)
    {
        ValidatePath(path, mustExist: false);
        return OpenFileImpl(path, mode, access, share);
    }

    /// <inheritdoc/>
    public IEnumerable<UPath> EnumeratePaths(
        UPath path,
        string searchPattern = "*",
        SearchDepth searchOption = SearchDepth.TopDirectoryOnly,
        SearchTarget searchTarget = SearchTarget.Both)
    {
        ValidatePath(path, mustExist: true);
        return EnumeratePathsImpl(path, searchPattern, searchOption, searchTarget);
    }

    /// <inheritdoc/>
    public IEnumerable<FileSystemItem> EnumerateItems(
        UPath path,
        SearchDepth searchOption = SearchDepth.TopDirectoryOnly,
        SearchPredicate? predicate = null)
    {
        ValidatePath(path, mustExist: true);
        return EnumerateItemsImpl(path, searchOption, predicate);
    }

    /// <inheritdoc/>
    public long GetFileLength(UPath path)
    {
        ValidatePath(path, mustExist: true);
        return GetFileLengthImpl(path);
    }

    /// <inheritdoc/>
    public DateTime GetLastWriteTime(UPath path)
    {
        ValidatePath(path, mustExist: true);
        return GetLastWriteTimeImpl(path);
    }

    /// <inheritdoc/>
    public DateTime GetCreationTime(UPath path)
    {
        ValidatePath(path, mustExist: true);
        return GetCreationTimeImpl(path);
    }

    /// <inheritdoc/>
    public DateTime GetLastAccessTime(UPath path)
    {
        ValidatePath(path, mustExist: true);
        return GetLastAccessTimeImpl(path);
    }

    /// <inheritdoc/>
    public void SetLastWriteTime(UPath path, DateTime time)
    {
        ValidatePath(path, mustExist: true);
        SetLastWriteTimeImpl(path, time);
    }

    /// <inheritdoc/>
    public void SetCreationTime(UPath path, DateTime time)
    {
        ValidatePath(path, mustExist: true);
        SetCreationTimeImpl(path, time);
    }

    /// <inheritdoc/>
    public void SetLastAccessTime(UPath path, DateTime time)
    {
        ValidatePath(path, mustExist: true);
        SetLastAccessTimeImpl(path, time);
    }

    /// <inheritdoc/>
    public FileAttributes GetAttributes(UPath path)
    {
        ValidatePath(path, mustExist: true);
        return GetAttributesImpl(path);
    }

    /// <inheritdoc/>
    public void SetAttributes(UPath path, FileAttributes attributes)
    {
        ValidatePath(path, mustExist: true);
        SetAttributesImpl(path, attributes);
    }

    /// <inheritdoc/>
    public FileEntry GetFileEntry(UPath path)
    {
        ValidatePath(path, mustExist: true);
        return FileEntry.Create(this, path);
    }

    /// <inheritdoc/>
    public DirectoryEntry GetDirectoryEntry(UPath path)
    {
        ValidatePath(path, mustExist: true);
        return DirectoryEntry.Create(this, path);
    }

    /// <inheritdoc/>
    public UPath ConvertPathFromInternal(string internalPath) => ConvertPathFromInternalImpl(internalPath);

    /// <inheritdoc/>
    public string ConvertPathToInternal(UPath path) => ConvertPathToInternalImpl(path);

    /// <summary>释放文件系统。默认无操作，子类按需覆写。</summary>
    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
