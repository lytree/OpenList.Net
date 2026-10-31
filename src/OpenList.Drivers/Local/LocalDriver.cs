using System.Text.Json.Serialization;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Drivers.Archive;
using OpenList.Vfs;

namespace OpenList.Drivers.Local;

/// <summary>
/// 本地文件系统驱动，挂载服务器文件系统上的某个目录。
/// 对应 OpenList Go 的 <c>drivers/local</c>。通过 <see cref="SharpCompressArchiveSupport"/>
/// 实现归档读取/解压接口（zip/rar/7z/tar/gz）。
/// </summary>
public sealed class LocalDriver : DriverBase, IFileSystemArchiveReader, IFileSystemArchiveDecompress, IFileSystemLocalPath, IThumbnailSwitch
{
    /// <summary>驱动名称。</summary>
    public override string DriverName => "Local";
    /// <summary>本地驱动支持写入，非只读。</summary>
    public override bool ReadOnly => false;
    /// <summary>本地驱动不属于云存储驱动器。</summary>
    public override bool IsCloudDriver => false;

    /// <summary>
    /// 该挂载是否允许出缩略图。默认 <c>true</c>：全局开关才是主开关，
    /// 驱动级配置只在管理员显式关掉时生效（旧版本该字段默认 false，会让本地盘静默不出图）。
    /// </summary>
    public bool ThumbnailEnabled => Addition.Thumbnail;

    /// <summary>
    /// 把驱动内相对路径映射为宿主机绝对路径（缩略图抽帧需要可随机读的输入）。
    /// </summary>
    /// <param name="path">相对挂载点的路径，例如 <c>/a/b.mp4</c>。</param>
    /// <param name="physicalPath">成功时为宿主机绝对路径。</param>
    /// <returns>映射成功且文件存在时为 <c>true</c>。</returns>
    public bool TryGetLocalPath(string path, out string physicalPath)
    {
        physicalPath = string.Empty;
        if (string.IsNullOrWhiteSpace(Addition.RootFolderPath)) return false;
        try
        {
            var root = Path.GetFullPath(Addition.RootFolderPath);
            var relative = path.Replace('\\', '/').TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(root, relative));

            // 越界校验：映射结果必须仍在根目录之内，否则 ../ 就能把宿主机任意文件喂给 ffmpeg。
            var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!File.Exists(full)) return false;
            physicalPath = full;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 列出压缩包内的归档条目。
    /// </summary>
    /// <param name="path">压缩包在挂载文件系统中的路径。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>归档条目列表。</returns>
    /// <exception cref="InvalidOperationException">驱动尚未初始化时抛出。</exception>
    public Task<IReadOnlyList<ArchiveEntry>> ListArchiveEntriesAsync(string path, CancellationToken ct = default)
    {
        if (System is null) throw new InvalidOperationException("driver not initialized");
        return Task.FromResult(SharpCompressArchiveSupport.ListEntries(System, path));
    }

    /// <summary>
    /// 打开归档内的单个条目（内联预览用）。
    /// </summary>
    /// <param name="archivePath">压缩包路径。</param>
    /// <param name="entryPath">归档内条目的相对路径（<c>/</c> 分隔，不含前导斜杠）。</param>
    /// <param name="password">可选的加密压缩包密码。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>可 Seek 的只读流；条目不存在时为 <c>null</c>。</returns>
    public Task<Stream?> OpenArchiveEntryAsync(
        string archivePath,
        string entryPath,
        string? password = null,
        CancellationToken ct = default)
    {
        if (System is null) throw new InvalidOperationException("driver not initialized");
        return Task.FromResult(SharpCompressArchiveSupport.OpenEntry(System, archivePath, entryPath, password));
    }

    /// <summary>
    /// 将压缩包解压到目标目录。
    /// </summary>
    /// <param name="archivePath">压缩包在挂载文件系统中的路径。</param>
    /// <param name="destPath">解压目标目录。</param>
    /// <param name="password">可选的加密压缩包密码。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>表示解压完成的任务。</returns>
    /// <exception cref="InvalidOperationException">驱动尚未初始化，或归档条目含不安全路径时抛出。</exception>
    public Task DecompressAsync(string archivePath, string destPath, string? password = null, CancellationToken ct = default)
    {
        if (System is null) throw new InvalidOperationException("driver not initialized");
        SharpCompressArchiveSupport.Decompress(System, archivePath, destPath, password);
        return Task.CompletedTask;
    }

    /// <summary>该驱动暴露给管理端配置的字段枚举。</summary>
    public override IEnumerable<DriverItemAttributes> ConfigItems => ConfigSchema;

    internal static readonly DriverItemAttributes[] ConfigSchema =
    [
        new()
        {
            Name = "root_folder_path",
            Help = "Root folder path on the server filesystem",
            Required = true,
            ValueType = typeof(string),
        },
        new()
        {
            Name = "thumbnail",
            Help = "Enable thumbnail generation for this mount",
            ValueType = typeof(bool),
            Default = true,
        },
    ];

    /// <summary>本地驱动专用配置项（通过 <see cref="InitAsync"/> 解析填充）。</summary>
    public LocalAddition Addition { get; private set; } = new();

    /// <summary>
    /// 初始化本地驱动：校验根目录、转换为物理文件系统。
    /// </summary>
    /// <param name="config">包含本地路径等字段的驱动配置。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>表示初始化完成的任务。</returns>
    /// <exception cref="InvalidOperationException">未配置 <c>root_folder_path</c> 时抛出。</exception>
    /// <exception cref="DirectoryNotFoundException">根目录不存在时抛出。</exception>
    public override Task InitAsync(DriverConfig config, CancellationToken ct = default)
    {
        Addition = config.GetAddition<LocalAddition>();
        if (string.IsNullOrWhiteSpace(Addition.RootFolderPath))
            throw new InvalidOperationException("root_folder_path is required");

        var root = Path.GetFullPath(Addition.RootFolderPath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"root folder not found: {root}");

        var physical = new PhysicalFileSystem();
        // PhysicalFileSystem 使用其操作系统相关的根路径；这里将磁盘上的绝对路径
        // 转换为该物理文件系统下的 VFS 绝对路径，再以其为根挂载子文件系统，从而把访问限制在根目录内。
        var vfsRoot = physical.ConvertPathFromInternal(root);
        System = new SubFileSystem(physical, vfsRoot);
        return Task.CompletedTask;
    }
}

/// <summary>本地驱动的配置项模型，映射 <c>root_folder_path</c> 等 JSON 输入字段。</summary>
public sealed class LocalAddition
{
    /// <summary>服务器文件系统上的根目录路径。</summary>
    [JsonPropertyName("root_folder_path")]
    public string RootFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// 是否启用缩略图生成。默认 <c>true</c>：缓存目录与开关统一由全局设置
    /// （<c>thumb_*</c>）管理，这里只保留"这个挂载参不参与"的一个粗粒度开关。
    /// </summary>
    [JsonPropertyName("thumbnail")]
    public bool Thumbnail { get; set; } = true;
}
