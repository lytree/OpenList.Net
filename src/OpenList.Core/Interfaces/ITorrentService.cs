using System.Text.Json.Serialization;
using OpenList.Core.Models;
using OpenList.Core.Torrent;

namespace OpenList.Core.Interfaces;

/// <summary>
/// torrent 解析与（驱动能力允许时的）秒传接口。对应 Go 的
/// <c>ParseTorrent</c> / <c>TorrentRapidUpload</c> / <c>UploadTorrentAndParse</c>。
/// <para>
/// 当前 .NET 实现：<see cref="ParseAsync"/> 与 <see cref="UploadParseAsync"/> 都直接返回
/// bencode 解析后的 <see cref="TorrentInfo"/>；<see cref="RapidUploadAsync"/> 需要驱动实现
/// 秒传能力（目前只有天翼云 PC 驱动具备），其他驱动返回 501。
/// </para>
/// </summary>
public interface ITorrentService
{
    /// <summary>
    /// 解析 base64 编码的 .torrent 文件，返回种子详情。
    /// 对应 Go <c>ParseTorrent</c>。
    /// </summary>
    Task<Resp<TorrentParsedResp>> ParseAsync(string torrentBase64, CancellationToken ct = default);

    /// <summary>
    /// 解析已上传的 .torrent 字节流，返回种子详情，并把原始字节回发为 base64（方便前端接力）。
    /// 对应 Go <c>UploadTorrentAndParse</c>。
    /// </summary>
    Task<Resp<TorrentUploadedResp>> UploadParseAsync(byte[] torrentBytes, CancellationToken ct = default);

    /// <summary>
    /// 把种子中的 CAS 信息用于秒传到指定路径的驱动（仅目标驱动支持时）。
    /// 当前 .NET 无天翼云驱动，秒传会在不支持的驱动上返回 501。
    /// 对应 Go <c>TorrentRapidUpload</c>。
    /// </summary>
    Task<Resp<TorrentRapidUploadResp>> RapidUploadAsync(string torrentBase64, string path, User user, CancellationToken ct = default);
}

/// <summary>
/// <see cref="ITorrentService.ParseAsync"/> 的响应载荷。
/// 字段命名与 Go <c>ParseTorrentResp</c> 对齐：snake_case 由 <c>[JsonPropertyName]</c> 提供。
/// </summary>
public sealed class TorrentParsedResp
{
    /// <summary>种子名称。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>所有文件总大小。</summary>
    [JsonPropertyName("total_size")]
    public long TotalSize { get; set; }
    /// <summary>分片大小（字节）。</summary>
    [JsonPropertyName("piece_length")]
    public long PieceLength { get; set; }
    /// <summary>分片数量。</summary>
    [JsonPropertyName("piece_count")]
    public int PieceCount { get; set; }
    /// <summary>info_hash 十六进制字符串（小写）。</summary>
    [JsonPropertyName("info_hash")]
    public string InfoHash { get; set; } = string.Empty;
    /// <summary>种子包含的文件信息。</summary>
    public List<TorrentFileInfo> Files { get; set; } = [];
    /// <summary>是否包含 CAS 扩展。</summary>
    [JsonPropertyName("has_cas")]
    public bool HasCas { get; set; }
    /// <summary>CAS 详情（无 CAS 时为 null）。</summary>
    public CasInfoResp? Cas { get; set; }
}

/// <summary><see cref="ITorrentService.UploadParseAsync"/> 的响应载荷。</summary>
public sealed class TorrentUploadedResp
{
    /// <summary>种子信息（与 <see cref="TorrentParsedResp"/> 同结构）。</summary>
    public TorrentParsedResp Info { get; set; } = new();
    /// <summary>原始 torrent 字节的 base64（方便前端接力秒传）。</summary>
    [JsonPropertyName("torrent_data")]
    public string TorrentData { get; set; } = string.Empty;
}

/// <summary><see cref="ITorrentService.RapidUploadAsync"/> 的响应载荷。</summary>
public sealed class TorrentRapidUploadResp
{
    /// <summary>人可读的结果消息。</summary>
    public string Message { get; set; } = string.Empty;
    /// <summary>秒传后生成的文件名。</summary>
    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = string.Empty;
    /// <summary>秒传后文件大小（字节）。</summary>
    [JsonPropertyName("file_size")]
    public long FileSize { get; set; }
}

/// <summary>种子中单个文件条目（对齐 Go <c>TorrentFileInfo</c>）。</summary>
public sealed class TorrentFileInfo
{
    /// <summary>相对路径（多文件模式）或文件名（单文件模式）。</summary>
    public string Path { get; set; } = string.Empty;
    /// <summary>大小（字节）。</summary>
    public long Size { get; set; }
}

/// <summary>CAS 扩展信息响应（对齐 Go <c>CASInfoResp</c>）。</summary>
public sealed class CasInfoResp
{
    /// <summary>整文件 MD5（大写十六进制）。</summary>
    [JsonPropertyName("file_md5")]
    public string FileMd5 { get; set; } = string.Empty;
    /// <summary>分片 MD5 的摘要（大写十六进制）。</summary>
    [JsonPropertyName("slice_md5")]
    public string SliceMd5 { get; set; } = string.Empty;
    /// <summary>分片大小（字节）。</summary>
    [JsonPropertyName("slice_size")]
    public long SliceSize { get; set; }
    /// <summary>云盘类型标识。</summary>
    public string Cloud { get; set; } = string.Empty;
}