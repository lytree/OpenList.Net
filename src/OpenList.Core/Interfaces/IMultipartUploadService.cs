using System.Text.Json.Serialization;
using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// 分片（Multipart）上传会话管理。与 OpenList Go 的
/// <c>internal/multipart</c> + <c>server/handles/multipart.go</c> 对应。前端
/// （src/pages/home/uploads/multipart.ts）驱动 init → chunk* → complete 流程，
/// 并支持状态轮询与断点续传。
/// </summary>
public interface IMultipartUploadService
{
    /// <summary>为给定路径+大小创建（或续传）一个分片上传会话。</summary>
    Task<Resp<MultipartInitResp>> InitAsync(MultipartInitRequest req, User user, CancellationToken ct = default);

    /// <summary>接收会话的单个分片。流控：窗口已满时返回 code 429。</summary>
    Task<Resp<MultipartSnapshot>> UploadChunkAsync(string uploadId, int chunkIndex, Stream body, CancellationToken ct = default);

    /// <summary>获取会话快照。会话不存在时返回 code 404（完成后即被回收）。</summary>
    Task<Resp<MultipartSnapshot>> StatusAsync(string uploadId, CancellationToken ct = default);

    /// <summary>将所有已接收分片刷写到目标存储路径。</summary>
    Task<Resp<MultipartSnapshot>> CompleteAsync(string uploadId, User user, CancellationToken ct = default);

    /// <summary>中止并丢弃会话（标记为已中止，释放已缓冲的分片）。</summary>
    Task<Resp> AbortAsync(string uploadId, CancellationToken ct = default);
}

/// <summary>
/// 分片上传会话初始化请求。
/// </summary>
public sealed class MultipartInitRequest
{
    /// <summary>目标存储路径。</summary>
    public string Path { get; set; } = "";
    /// <summary>文件总大小（字节）。</summary>
    public long Size { get; set; }
    /// <summary>每个分片的大小（字节）。</summary>
    public long ChunkSize { get; set; }
    /// <summary>可选的文件 MD5 值。</summary>
    public string? Md5 { get; set; }
    /// <summary>可选的文件 SHA1 值。</summary>
    public string? Sha1 { get; set; }
    /// <summary>可选的文件 SHA256 值。</summary>
    public string? Sha256 { get; set; }
}

/// <summary>
/// 分片上传会话的当前状态快照。
/// </summary>
public class MultipartSnapshot
{
    /// <summary>上传会话 id。</summary>
    [JsonPropertyName("upload_id")]
    public string UploadId { get; set; } = "";

    /// <summary>会话状态：receiving | completed | failed_retriable | failed_permanent | aborted。</summary>
    public string State { get; set; } = "receiving";

    /// <summary>重试次数。</summary>
    public int Attempt { get; set; }
    /// <summary>目标存储路径。</summary>
    public string Path { get; set; } = "";
    /// <summary>文件总大小（字节）。</summary>
    public long Size { get; set; }

    /// <summary>分片大小（字节）。</summary>
    [JsonPropertyName("chunk_size")]
    public long ChunkSize { get; set; }

    /// <summary>总分片数。</summary>
    [JsonPropertyName("total_chunks")]
    public int TotalChunks { get; set; }

    /// <summary>已接收分片的区间列表，以闭合的 [lo, hi] 表示。</summary>
    [JsonPropertyName("received")]
    public List<int[]> Received { get; set; } = new();

    /// <summary>已接收的字节数。</summary>
    [JsonPropertyName("received_bytes")]
    public long ReceivedBytes { get; set; }

    /// <summary>最大连续已接收分片索引 + 1（未接收时为 0）。</summary>
    public int Frontier { get; set; }

    /// <summary>写存储时的进度（0-100）。</summary>
    [JsonPropertyName("storage_progress")]
    public int StorageProgress { get; set; }

    /// <summary>错误信息，可为空。</summary>
    public string? Error { get; set; }
}

/// <summary>
/// 初始化成功的响应，在 <see cref="MultipartSnapshot"/> 基础上增加是否续传的标记。
/// </summary>
public sealed class MultipartInitResp : MultipartSnapshot
{
    /// <summary>是否为续传现有会话。</summary>
    public bool Resumed { get; set; }
}
