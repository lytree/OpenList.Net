namespace OpenList.Core.Drivers;

/// <summary>驱动可为文件提供直接下载/代理 URL（云端驱动）。</summary>
public interface IFileSystemLinkProvider
{
    /// <summary>返回给定路径的直接链接 URL；不支持时返回 null。</summary>
    Task<Uri?> GetLinkAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// 获取文件的访问通道，供下载端点区分「直链跳转」（<c>/d/</c>）与「服务端代理」（<c>/p/</c>）。
    /// </summary>
    /// <param name="path">
    /// <b>相对挂载点</b>的驱动内路径（调用方经 <c>MountService.ToDriverRelativePath</c> 转换），
    /// 例如挂载于 <c>/ali</c> 的 <c>/ali/a.txt</c> 传入 <c>/a.txt</c>。
    /// </param>
    /// <param name="proxy">
    /// <c>true</c> 表示调用方需要服务端代理流（<c>/p/</c>，用于必须由服务器转发或需要
    /// 精确 Range 播放的场景）；<c>false</c> 表示优先直链（<c>/d/</c>）。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>可用的通道；返回 null 表示驱动不参与本次下载，调用方回退为通用 VFS 流式传输。</returns>
    /// <remarks>
    /// 默认实现返回 null，即「本驱动不提供专用下载通道」。这是刻意保守的默认值：
    /// <see cref="GetLinkAsync"/> 在 S3 / WebDAV 上返回的是**未签名**地址，直接 302 会让
    /// 原本可用的下载变成 403；只有显式实现本方法的驱动（云盘驱动）才进入直链/代理下载链路。
    /// </remarks>
    Task<FileLink?> GetFileLinkAsync(string path, bool proxy, CancellationToken ct = default)
        => Task.FromResult<FileLink?>(null);
}

/// <summary>
/// 驱动为单个文件提供的访问通道：直链（可 302 跳转）或服务端代理流（按区间读取）。
/// 对应 Go <c>model.Link</c> 的 <c>URL</c> / <c>RangeReader</c> / <c>Expiration</c> 三个字段。
/// </summary>
public sealed class FileLink
{
    /// <summary>
    /// 可直接重定向给客户端的直链。代理模式下（或驱动只支持代理时）为空，
    /// 此时由 <see cref="RangeReader"/> 承担内容传输。
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// 服务端代理模式下的区间读取器。直链足够用时为空 —— 两者至少提供一个才有意义。
    /// </summary>
    public ProxyRangeReader? RangeReader { get; init; }

    /// <summary>直链/通道的过期时间，供调用方决定是否复用；未知时为 null。</summary>
    public DateTimeOffset? Expiration { get; init; }

    /// <summary>是否可用于直链跳转（<c>/d/</c> 是否应 302）。</summary>
    public bool HasUrl => !string.IsNullOrEmpty(Url);
}

/// <summary>
/// 代理区间读取器：按 <c>[start, start+length)</c> 打开一次内容流。
/// <para>
/// 语义对齐 Go <c>stream.RangeReaderFunc</c>：实现方在返回流的**整个生命周期**内持有必需资源
/// （例如阿里云回调并发许可），调用方释放流即代表本次区间读取结束，实现方应据此归还资源。
/// </para>
/// </summary>
/// <param name="start">起始字节偏移。</param>
/// <param name="length">要读取的字节数；实现方负责收敛到文件末尾。</param>
/// <param name="ct">取消令牌；取消应连带释放已打开的远端流。</param>
/// <returns>指向该区间的只读流。</returns>
public delegate Task<Stream> ProxyRangeReader(long start, long length, CancellationToken ct);

/// <summary>驱动支持通过流或表单上传文件。</summary>
public interface IFileSystemUploader
{
    /// <summary>以流形式写入指定路径。</summary>
    Task PutAsync(string path, Stream stream, CancellationToken ct = default);
    /// <summary>以表单形式（带文件名）写入指定路径。</summary>
    Task PutFormAsync(string path, Stream stream, string fileName, CancellationToken ct = default);
}

/// <summary>驱动除 <see cref="Models.Obj"/> 外还暴露额外的文件详情。</summary>
public interface IFileSystemWithDetails
{
    /// <summary>获取指定路径的额外详情字典。</summary>
    Task<Dictionary<string, object?>> GetDetailsAsync(string path, CancellationToken ct = default);
}

/// <summary>驱动可读取归档内容（列出条目 + 读取单个条目）。</summary>
public interface IFileSystemArchiveReader
{
    /// <summary>列出归档内的全部条目。</summary>
    Task<IReadOnlyList<ArchiveEntry>> ListArchiveEntriesAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// 打开归档内单个条目，返回<b>可 Seek</b> 的只读流（调用方负责 Dispose，释放时清理临时文件）。
    /// 找不到条目（或路径不安全）返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 必须可 Seek：内联预览要支持 HTTP Range（拖动视频进度条、分段读取文本），
    /// 而压缩流的条目是前向只读的。实现因此把条目落到临时文件再返回 <see cref="FileStream"/>。
    /// </remarks>
    Task<Stream?> OpenArchiveEntryAsync(
        string archivePath,
        string entryPath,
        string? password = null,
        CancellationToken ct = default);
}

/// <summary>驱动可原地解压归档。</summary>
public interface IFileSystemArchiveDecompress
{
    /// <summary>将归档解压到目标路径；<paramref name="password"/> 为加密归档密码，可为空。</summary>
    Task DecompressAsync(string archivePath, string destPath, string? password = null, CancellationToken ct = default);
}

/// <summary>驱动支持通过预签名 URL 或分片上传 API 进行直传。</summary>
public interface IFileSystemDirectUploader
{
    /// <summary>获取给定路径+大小的直传信息。</summary>
    Task<DirectUploadInfo> GetDirectUploadInfoAsync(string path, long size, CancellationToken ct = default);
}

/// <summary>
/// 归档内的单个条目。
/// </summary>
public sealed class ArchiveEntry
{
    /// <summary>条目名称（含相对路径）。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>大小（字节）。</summary>
    public long Size { get; set; }
    /// <summary>是否为目录。</summary>
    public bool IsDir { get; set; }
    /// <summary>修改时间。</summary>
    public DateTimeOffset Modified { get; set; }
}

/// <summary>
/// 直传（客户端侧上传）所需的信息。
/// </summary>
public sealed class DirectUploadInfo
{
    /// <summary>上传目标 URL。</summary>
    public string Url { get; set; } = string.Empty;
    /// <summary>上传时需要的请求头。</summary>
    public Dictionary<string, string> Headers { get; set; } = new();
    /// <summary>使用的 HTTP 方法，可为空。</summary>
    public string? Method { get; set; }
}

/// <summary>
/// 驱动能把挂载路径映射回宿主机上的真实文件路径（本地驱动）。
/// <para>
/// 存在的唯一理由是缩略图抽帧：ffmpeg 需要<b>可随机读</b>的输入，而 VFS 的
/// <c>OpenFile</c> 在云盘上是不可 Seek 的顺序流。给 ffmpeg 一个真实路径，
/// 它自己用 pread 处理，比我们绕一层 AVIO 回调简单得多。
/// </para>
/// <para>
/// 实现方必须自己做越界校验（不能因为路径里带 <c>../</c> 就跑到挂载根之外）。
/// </para>
/// </summary>
public interface IFileSystemLocalPath
{
    /// <summary>
    /// 把驱动内相对路径（<c>/a/b.mp4</c>）映射为宿主机绝对路径。
    /// </summary>
    /// <param name="path">相对挂载点的驱动内路径。</param>
    /// <param name="physicalPath">映射成功时为宿主机绝对路径，否则为空串。</param>
    /// <returns>映射成功且文件存在时返回 <c>true</c>。</returns>
    bool TryGetLocalPath(string path, out string physicalPath);
}

/// <summary>
/// 驱动级的缩略图开关：存储配置里显式关掉时，该挂载下的条目不再下发 <c>thumb</c>。
/// <para>
/// 只有声明了这个接口的驱动才参与判定；没有声明的驱动一律按「全局开关为准」处理，
/// 避免每个驱动各复制一个字段、最后变成「为什么这个盘不出图」要在两处排查。
/// </para>
/// </summary>
public interface IThumbnailSwitch
{
    /// <summary>该挂载是否允许生成/下发缩略图。</summary>
    bool ThumbnailEnabled { get; }
}
