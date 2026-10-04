using System.Text.Json.Serialization;
using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// 文件系统操作：列表、获取、建目录、重命名、移动、复制、删除、上传、归档等。
/// 与 OpenList Go 的 <c>server/handles/fsread.go</c> + <c>fsmanage.go</c> + <c>archive.go</c> 对应。
/// </summary>
public interface IFileSystemService
{
    /// <summary>列出目录内容。</summary>
    Task<Resp<FsListResp>> ListAsync(FsListReq req, User user, CancellationToken ct = default);
    /// <summary>获取单个文件/目录的详细信息（含下载直链）。</summary>
    Task<Resp<FsGetResp>> GetAsync(FsGetReq req, User user, CancellationToken ct = default);
    /// <summary>创建目录。</summary>
    Task<Resp> MkdirAsync(string path, User user, CancellationToken ct = default);
    /// <summary>重命名单个文件/目录。</summary>
    Task<Resp> RenameAsync(string oldPath, string newPath, User user, CancellationToken ct = default);
    /// <summary>批量重命名。</summary>
    Task<Resp> BatchRenameAsync(IReadOnlyList<BatchRenameItem> items, User user, CancellationToken ct = default);
    /// <summary>按正则表达式批量重命名（srcReg → newReg）。</summary>
    Task<Resp> RegexRenameAsync(string srcDir, string srcReg, string newReg, User user, CancellationToken ct = default);
    /// <summary>移动多个源路径到目标目录（同步执行，保留用于内部测试）。</summary>
    Task<Resp> MoveAsync(IReadOnlyList<string> srcPaths, string destDir, User user, CancellationToken ct = default);
    /// <summary>递归移动单个路径到目标目录。</summary>
    Task<Resp> RecursiveMoveAsync(string srcPath, string destDir, User user, CancellationToken ct = default);
    /// <summary>复制多个源路径到目标目录（同步执行，保留用于内部测试）。</summary>
    Task<Resp> CopyAsync(IReadOnlyList<string> srcPaths, string destDir, User user, CancellationToken ct = default);
    /// <summary>
    /// 批量移动（任务化）：与 Go <c>FsMove</c> 对齐，遵循 <paramref name="req"/> 中的 <c>overwrite/skip_existing/merge</c> 规则，
    /// 返回新建的后台任务列表。
    /// </summary>
    Task<Resp<FsManageResp>> MoveBatchAsync(MoveCopyReq req, User user, CancellationToken ct = default);
    /// <summary>
    /// 批量复制（任务化）：与 Go <c>FsCopy</c> 对齐，<c>merge=true</c> 时同名目录会被合并（保留并继续），
    /// 返回新建的后台任务列表。
    /// </summary>
    Task<Resp<FsManageResp>> CopyBatchAsync(MoveCopyReq req, User user, CancellationToken ct = default);
    /// <summary>删除多个路径。</summary>
    Task<Resp> RemoveAsync(IReadOnlyList<string> paths, User user, CancellationToken ct = default);
    /// <summary>删除空的目录。</summary>
    Task<Resp> RemoveEmptyDirectoryAsync(string path, User user, CancellationToken ct = default);
    /// <summary>以流形式上传内容到指定路径。</summary>
    Task<Resp> PutStreamAsync(string path, Stream stream, User user, CancellationToken ct = default);
    /// <summary>以表单形式上传（带文件名）。</summary>
    Task<Resp> PutFormAsync(string path, Stream stream, string fileName, User user, CancellationToken ct = default);
    /// <summary>获取直传（客户端侧上传）所需的信息。</summary>
    Task<Resp<DirectUploadInfo>> GetDirectUploadInfoAsync(string path, long size, User user, CancellationToken ct = default);
    /// <summary>生成文件的下载链接。</summary>
    Task<Resp<LinkResp>> LinkAsync(string path, User user, CancellationToken ct = default);
    /// <summary>添加离线下载请求。</summary>
    Task<Resp> AddOfflineDownloadAsync(OfflineDownloadReq req, User user, CancellationToken ct = default);
    /// <summary>获取归档的元信息（受支持的扩展名等）。</summary>
    Task<Resp<ArchiveMetaResp>> ArchiveMetaAsync(string path, User user, CancellationToken ct = default);
    /// <summary>列出归档内的条目。</summary>
    Task<Resp<ArchiveListResp>> ArchiveListAsync(string path, string? password, User user, CancellationToken ct = default);
    /// <summary>将归档解压到目标路径。</summary>
    Task<Resp> ArchiveDecompressAsync(string archivePath, string destPath, string? password, User user, CancellationToken ct = default);
    /// <summary>列出某路径下的所有子目录。</summary>
    Task<Resp<DirsResp>> DirsAsync(string path, User user, CancellationToken ct = default);
    /// <summary>执行其它杂项文件系统操作。</summary>
    Task<Resp> OtherAsync(string path, string action, User user, CancellationToken ct = default);
    /// <summary>按关键字搜索文件。</summary>
    Task<Resp<SearchResp>> SearchAsync(SearchReq req, User user, CancellationToken ct = default);
}

/// <summary>
/// 目录列表请求。
/// </summary>
public sealed class FsListReq
{
    /// <summary>目标路径，默认根 "/"。</summary>
    public string Path { get; set; } = "/";
    /// <summary>访问密码（有元数据密码时），可为空。</summary>
    public string? Password { get; set; }
    /// <summary>是否强制刷新缓存。</summary>
    public bool Refresh { get; set; }
    /// <summary>页码，从 1 开始。</summary>
    public int Page { get; set; } = 1;
    /// <summary>每页条数，默认 30。</summary>
    [JsonPropertyName("per_page")]
    public int PerPage { get; set; } = 30;
}

/// <summary>
/// 目录列表响应。
/// </summary>
public sealed class FsListResp
{
    /// <summary>当前页条目列表。</summary>
    public List<Obj> Content { get; set; } = [];
    /// <summary>符合条件的条目总数。</summary>
    public int Total { get; set; }
    /// <summary>目录 readme，可为空。</summary>
    public string? Readme { get; set; }
    /// <summary>目录自定义头部，可为空。</summary>
    public string? Header { get; set; }
    /// <summary>当前用户在该目录是否可写。</summary>
    public bool Write { get; set; }
    /// <summary>是否可绕过写入内容限制。</summary>
    [JsonPropertyName("write_content_bypass")]
    public bool WriteContentBypass { get; set; }
    /// <summary>提供者标识，可为空。</summary>
    public string? Provider { get; set; }
    /// <summary>可用的直传工具列表，可为空。</summary>
    [JsonPropertyName("direct_upload_tools")]
    public List<string>? DirectUploadTools { get; set; }
}

/// <summary>
/// 获取单个条目详情的请求。
/// </summary>
public sealed class FsGetReq
{
    /// <summary>目标路径，默认根 "/"。</summary>
    public string Path { get; set; } = "/";
    /// <summary>访问密码，可为空。</summary>
    public string? Password { get; set; }
}

/// <summary>
/// 获取单个条目详情的响应，继承自 <see cref="Obj"/>。
/// </summary>
public sealed class FsGetResp : Obj
{
    /// <summary>原始文件 URL（下载直链）。</summary>
    [JsonPropertyName("raw_url")]
    public string? RawUrl { get; set; }
    /// <summary>readme 内容，可为空。</summary>
    public string? Readme { get; set; }
    /// <summary>自定义头部内容，可为空。</summary>
    public string? Header { get; set; }
    /// <summary>提供者标识，可为空。</summary>
    public string? Provider { get; set; }
    /// <summary>相关条目列表（如同一影片的多集）。</summary>
    public List<Obj> Related { get; set; } = [];
}

/// <summary>
/// 单个批量重命名的条目：旧名 → 新名。
/// </summary>
public sealed class BatchRenameItem
{
    /// <summary>旧名称。</summary>
    public string SrcName { get; set; } = string.Empty;
    /// <summary>新名称。</summary>
    public string NewName { get; set; } = string.Empty;
}

/// <summary>
/// 下载链接响应。
/// </summary>
public sealed class LinkResp
{
    /// <summary>下载 URL。</summary>
    public string Url { get; set; } = string.Empty;
    /// <summary>请求下载时需要的额外头部，可为空。</summary>
    public string? Header { get; set; }
}

/// <summary>
/// 离线下载请求载荷。
/// </summary>
public sealed class OfflineDownloadReq
{
    /// <summary>要下载的 URL 列表。</summary>
    [JsonPropertyName("urls")]
    public List<string> Urls { get; set; } = [];
    /// <summary>保存目标路径。</summary>
    public string Path { get; set; } = string.Empty;
    /// <summary>使用的下载工具（aria2/qbit/transmission 等），可为空表示默认。</summary>
    public string? Tool { get; set; }
    /// <summary>下载完成后的删除策略。</summary>
    [JsonPropertyName("delete_policy")]
    public int DeletePolicy { get; set; }
}

/// <summary>
/// 归档元信息响应。
/// </summary>
public sealed class ArchiveMetaResp
{
    /// <summary>受支持的归档扩展名列表。</summary>
    public string[] SupportedExtensions { get; set; } = [];
}

/// <summary>
/// 归档内条目列表响应。
/// </summary>
public sealed class ArchiveListResp
{
    /// <summary>当前页的条目列表。</summary>
    public List<ArchiveEntry> Content { get; set; } = [];
    /// <summary>条目总数。</summary>
    public int Total { get; set; }
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
/// 子目录列表响应。
/// </summary>
public sealed class DirsResp
{
    /// <summary>子目录路径列表。</summary>
    public List<string> Dirs { get; set; } = [];
}

/// <summary>
/// 搜索请求。
/// </summary>
public sealed class SearchReq
{
    /// <summary>搜索的父目录，默认根 "/"。</summary>
    public string Parent { get; set; } = "/";
    /// <summary>搜索关键字。</summary>
    public string Keywords { get; set; } = string.Empty;
    /// <summary>搜索范围（前端 scope：0=默认，……）。当前搜索实现接受但忽略该字段。</summary>
    public int Scope { get; set; }
    /// <summary>是否递归搜索子目录。</summary>
    public bool Recursive { get; set; } = true;
    /// <summary>页码。</summary>
    public int Page { get; set; } = 1;
    /// <summary>每页条数。</summary>
    [JsonPropertyName("per_page")]
    public int PerPage { get; set; } = 30;
    /// <summary>访问密码，可为空。</summary>
    public string? Password { get; set; }
}

/// <summary>
/// 搜索响应。
/// </summary>
public sealed class SearchResp
{
    /// <summary>当前页结果列表。</summary>
    public List<SearchNode> Content { get; set; } = [];
    /// <summary>结果总数。</summary>
    public int Total { get; set; }
}

/// <summary>扁平化的搜索结果节点，与前端的 SearchNode 类型一致。</summary>
public sealed class SearchNode
{
    /// <summary>父目录路径。</summary>
    public string Parent { get; set; } = string.Empty;
    /// <summary>条目名称。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>是否为目录。</summary>
    [JsonPropertyName("is_dir")]
    public bool IsDir { get; set; }
    /// <summary>大小（字节）。</summary>
    public long Size { get; set; }
    /// <summary>完整路径。</summary>
    public string Path { get; set; } = string.Empty;
    /// <summary>文件类型编码。</summary>
    public int Type { get; set; }
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
/// 批量移动 / 复制操作的请求体。对应 Go <c>MoveCopyReq</c>。
/// 端点 <c>/fs/move</c> 与 <c>/fs/copy</c> 共用此结构。
/// </summary>
public sealed class MoveCopyReq
{
    /// <summary>源目录（绝对虚拟路径）。</summary>
    [JsonPropertyName("src_dir")]
    public string SrcDir { get; set; } = string.Empty;
    /// <summary>目标目录（绝对虚拟路径）。</summary>
    [JsonPropertyName("dst_dir")]
    public string DstDir { get; set; } = string.Empty;
    /// <summary>要移动/复制的相对名称列表（相对 src_dir）。</summary>
    public List<string> Names { get; set; } = [];
    /// <summary>目标已存在同名时是否直接覆盖。</summary>
    public bool Overwrite { get; set; }
    /// <summary>目标已存在同名时是否跳过（覆盖优先）。</summary>
    [JsonPropertyName("skip_existing")]
    public bool SkipExisting { get; set; }
    /// <summary>复制时是否把同名目录合并（移动端忽略）。</summary>
    public bool Merge { get; set; }
}

/// <summary>
/// 批量移动 / 复制的返回载荷。对应 Go
/// <c>{ message: "Successfully created N move task(s)", tasks: [...] }</c>。
/// </summary>
public sealed class FsManageResp
{
    /// <summary>人可读的结果消息（任务数 / 立即完成等）。</summary>
    public string Message { get; set; } = string.Empty;
    /// <summary>本次操作创建的后台任务列表（同步完成时为空数组）。</summary>
    public List<TaskInfo> Tasks { get; set; } = [];
}
