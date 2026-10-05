namespace OpenList.Vfs;

/// <summary>目录枚举的递归范围。</summary>
public enum SearchDepth
{
    /// <summary>仅枚举当前目录的直接子项。</summary>
    TopDirectoryOnly = 0,
    /// <summary>递归枚举整棵子树。</summary>
    AllDirectories = 1,
}

/// <summary>目录枚举的目标类型过滤。</summary>
public enum SearchTarget
{
    /// <summary>同时返回文件与目录。</summary>
    Both = 0,
    /// <summary>仅返回文件。</summary>
    File = 1,
    /// <summary>仅返回目录。</summary>
    Directory = 2,
}

/// <summary>
/// 枚举条目时的过滤谓词。接收可变的 <see cref="FileSystemItem"/>，
/// 返回 <c>false</c> 表示该项应被跳过。用于让实现方在一次遍历中复用已获取的元数据。
/// </summary>
/// <param name="item">待判断的条目。</param>
/// <returns>保留该项返回 <c>true</c>。</returns>
public delegate bool SearchPredicate(ref FileSystemItem item);

/// <summary>
/// 文件系统中的条目元数据快照。
/// <para>
/// 用于 <c>EnumerateItems</c> 一次性返回「路径 + 属性 + 大小 + 时间」，
/// 避免调用方对每个条目再单独查询（那会退化成 N+1 次远程请求）。
/// </para>
/// </summary>
public sealed class FileSystemItem
{
    /// <summary>所属文件系统（可为 null，例如从挂载点汇总时）。</summary>
    public IFileSystem? FileSystem { get; init; }

    /// <summary>条目路径。</summary>
    public UPath Path { get; init; }

    /// <summary>文件属性。</summary>
    public FileAttributes Attributes { get; init; }

    /// <summary>长度（字节）；目录为 0。</summary>
    public long Length { get; init; }

    /// <summary>最后写入时间。</summary>
    public DateTime LastWriteTime { get; init; }

    /// <summary>创建时间。</summary>
    public DateTime CreationTime { get; init; }

    /// <summary>最后访问时间。</summary>
    public DateTime LastAccessTime { get; init; }

    /// <summary>是否为目录。</summary>
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
}

/// <summary>
/// 文件系统中的条目基类（FileSystemEntry）。
/// <para>
/// 这是 <see cref="IFileSystem.GetFileEntry"/> / <c>GetDirectoryEntry</c> 的返回类型，
/// 以属性形式暴露 原本需通过索引器 + 强转才能拿到的常用信息。
/// </para>
/// </summary>
public abstract class FileSystemEntry
{
    /// <summary>所属文件系统。</summary>
    public IFileSystem FileSystem { get; }

    /// <summary>条目路径。</summary>
    public UPath Path { get; }

    /// <summary>是否为目录。</summary>
    public abstract bool IsDirectory { get; }

    /// <summary>最后写入时间。</summary>
    public DateTime LastWriteTime { get; }

    /// <summary>创建时间。</summary>
    public DateTime CreationTime { get; }

    /// <summary>最后访问时间。</summary>
    public DateTime LastAccessTime { get; }

    /// <summary>文件属性。</summary>
    public FileAttributes Attributes { get; }

    /// <summary>条目名称（末级名称）。</summary>
    public string Name => Path.GetName();

    /// <summary>由文件系统查询元数据构造条目。</summary>
    /// <param name="fileSystem">所属文件系统。</param>
    /// <param name="path">条目路径。</param>
    /// <param name="attributes">文件属性。</param>
    /// <param name="lastWriteTime">最后写入时间。</param>
    /// <param name="creationTime">创建时间。</param>
    /// <param name="lastAccessTime">最后访问时间。</param>
    protected FileSystemEntry(
        IFileSystem fileSystem,
        UPath path,
        FileAttributes attributes,
        DateTime lastWriteTime,
        DateTime creationTime,
        DateTime lastAccessTime)
    {
        FileSystem = fileSystem;
        Path = path;
        Attributes = attributes;
        LastWriteTime = lastWriteTime;
        CreationTime = creationTime;
        LastAccessTime = lastAccessTime;
    }
}

/// <summary>文件条目。</summary>
public sealed class FileEntry : FileSystemEntry
{
    /// <summary>文件长度（字节）。</summary>
    public long Length { get; }

    /// <inheritdoc/>
    public override bool IsDirectory => false;

    /// <summary>查询文件元数据并构造条目。</summary>
    public static FileEntry Create(IFileSystem fs, UPath path) => new(
        fs,
        path,
        fs.GetFileLength(path),
        fs.GetLastWriteTime(path),
        fs.GetCreationTime(path),
        fs.GetLastAccessTime(path));

    /// <summary>内部构造。</summary>
    internal FileEntry(
        IFileSystem fileSystem,
        UPath path,
        long length,
        DateTime lastWriteTime,
        DateTime creationTime,
        DateTime lastAccessTime)
        : base(fileSystem, path, FileAttributes.Normal, lastWriteTime, creationTime, lastAccessTime)
    {
        Length = length;
    }
}

/// <summary>目录条目。</summary>
public sealed class DirectoryEntry : FileSystemEntry
{
    /// <inheritdoc/>
    public override bool IsDirectory => true;

    /// <summary>查询目录元数据并构造条目。</summary>
    public static DirectoryEntry Create(IFileSystem fs, UPath path) => new(
        fs,
        path,
        fs.GetLastWriteTime(path),
        fs.GetCreationTime(path),
        fs.GetLastAccessTime(path));

    /// <summary>内部构造。</summary>
    internal DirectoryEntry(
        IFileSystem fileSystem,
        UPath path,
        DateTime lastWriteTime,
        DateTime creationTime,
        DateTime lastAccessTime)
        : base(fileSystem, path, FileAttributes.Directory, lastWriteTime, creationTime, lastAccessTime)
    {
    }
}
