namespace OpenList.Core.Drivers;

/// <summary>驱动可为文件提供直接下载/代理 URL（云端驱动）。</summary>
public interface IFileSystemLinkProvider
{
    /// <summary>返回给定路径的直接链接 URL；不支持时返回 null。</summary>
    Task<Uri?> GetLinkAsync(string path, CancellationToken ct = default);
}

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

/// <summary>驱动可读取归档内容（列出条目）。</summary>
public interface IFileSystemArchiveReader
{
    /// <summary>列出归档内的全部条目。</summary>
    Task<IReadOnlyList<ArchiveEntry>> ListArchiveEntriesAsync(string path, CancellationToken ct = default);
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
