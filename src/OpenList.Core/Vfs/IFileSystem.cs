namespace OpenList.Vfs;

/// <summary>
/// 虚拟文件系统抽象（IFileSystem）。
/// <para>
/// 以同步 API 暴露文件与目录操作。所有实现（含 <see cref="FileSystem"/> 抽象基类）
/// 都遵循「公共方法负责路径校验与前置检查，<c>*Impl</c> 虚方法只做实际 I/O」的分工，
/// 这样驱动器实现无需重复编写存在性检查。
/// </para>
/// </summary>
public interface IFileSystem : IDisposable
{
    /// <summary>路径分隔符（固定为 <c>/</c>）。</summary>
    char DirectorySeparator { get; }

    /// <summary>路径是否区分大小写。</summary>
    bool IsCaseSensitive { get; }

    // --- 存在性 ---

    /// <summary>判断路径是否指向已存在的目录。</summary>
    bool DirectoryExists(UPath path);

    /// <summary>判断路径是否指向已存在的文件。</summary>
    bool FileExists(UPath path);

    // --- 目录操作 ---

    /// <summary>创建目录；已存在时抛出 <see cref="IOException"/>。</summary>
    void CreateDirectory(UPath path);

    /// <summary>删除目录。</summary>
    /// <param name="path">目录路径。</param>
    /// <param name="isRecursive">是否递归删除子项。</param>
    void DeleteDirectory(UPath path, bool isRecursive = false);

    /// <summary>移动/重命名目录。</summary>
    void MoveDirectory(UPath srcPath, UPath destPath);

    /// <summary>复制目录。</summary>
    /// <param name="srcPath">源目录。</param>
    /// <param name="destPath">目标目录。</param>
    /// <param name="overwrite">目标已存在时是否覆盖。</param>
    void CopyDirectory(UPath srcPath, UPath destPath, bool overwrite = false);

    // --- 文件操作 ---

    /// <summary>删除文件。</summary>
    void DeleteFile(UPath path);

    /// <summary>移动/重命名文件。</summary>
    void MoveFile(UPath srcPath, UPath destPath);

    /// <summary>复制文件。</summary>
    /// <param name="srcPath">源文件。</param>
    /// <param name="destPath">目标文件。</param>
    /// <param name="overwrite">目标已存在时是否覆盖。</param>
    void CopyFile(UPath srcPath, UPath destPath, bool overwrite = false);

    /// <summary>打开文件流。调用方负责释放返回的流。</summary>
    /// <param name="path">文件路径。</param>
    /// <param name="mode">打开模式。</param>
    /// <param name="access">访问模式。</param>
    /// <param name="share">共享模式。</param>
    Stream OpenFile(UPath path, FileMode mode, FileAccess access, FileShare share = FileShare.None);

    // --- 枚举 ---

    /// <summary>
    /// 枚举目录下的子路径。默认仅当前层、匹配全部、按名称通配 <c>*</c>。
    /// </summary>
    /// <param name="path">目录路径。</param>
    /// <param name="searchPattern">名称通配符（<c>*</c> / <c>?</c>）。</param>
    /// <param name="searchOption">是否递归。</param>
    /// <param name="searchTarget">返回文件、目录或两者。</param>
    IEnumerable<UPath> EnumeratePaths(
        UPath path,
        string searchPattern = "*",
        SearchDepth searchOption = SearchDepth.TopDirectoryOnly,
        SearchTarget searchTarget = SearchTarget.Both);

    /// <summary>枚举目录下的子项（含元数据），避免逐项再查询。</summary>
    /// <param name="path">目录路径。</param>
    /// <param name="searchOption">是否递归。</param>
    /// <param name="predicate">可选过滤谓词。</param>
    IEnumerable<FileSystemItem> EnumerateItems(
        UPath path,
        SearchDepth searchOption = SearchDepth.TopDirectoryOnly,
        SearchPredicate? predicate = null);

    // --- 元数据 ---

    /// <summary>获取文件长度（字节）。</summary>
    long GetFileLength(UPath path);

    /// <summary>获取最后写入时间。</summary>
    DateTime GetLastWriteTime(UPath path);

    /// <summary>获取创建时间。</summary>
    DateTime GetCreationTime(UPath path);

    /// <summary>获取最后访问时间。</summary>
    DateTime GetLastAccessTime(UPath path);

    /// <summary>设置最后写入时间。</summary>
    void SetLastWriteTime(UPath path, DateTime time);

    /// <summary>设置创建时间。</summary>
    void SetCreationTime(UPath path, DateTime time);

    /// <summary>设置最后访问时间。</summary>
    void SetLastAccessTime(UPath path, DateTime time);

    /// <summary>获取文件属性。</summary>
    FileAttributes GetAttributes(UPath path);

    /// <summary>设置文件属性。</summary>
    void SetAttributes(UPath path, FileAttributes attributes);

    /// <summary>获取文件条目（含元数据）。</summary>
    FileEntry GetFileEntry(UPath path);

    /// <summary>获取目录条目（含元数据）。</summary>
    DirectoryEntry GetDirectoryEntry(UPath path);

    // --- 路径转换 ---

    /// <summary>把外部路径字符串转换为本文件系统的路径。</summary>
    UPath ConvertPathFromInternal(string internalPath);

    /// <summary>把本文件系统的路径转换为外部路径字符串。</summary>
    string ConvertPathToInternal(UPath path);
}

/// <summary>
/// 文件系统监视器句柄（IFileSystemWatcher）。
/// <para>
/// 本项目未使用文件系统监视能力，抽象保留仅为兼容接口形状：
/// 网络驱动均不实现 <c>WatchImpl</c>，无需创建监视器。
/// </para>
/// </summary>
public interface IFileSystemWatcher : IDisposable
{
}
