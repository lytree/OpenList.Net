using System.Text.Json.Serialization;
using OpenList.Vfs;

namespace OpenList.Core.Models;

/// <summary>
/// 统一的文件/目录对象，由各存储驱动和接口端点返回。
/// 与 OpenList Go 的 <c>server/handles.ObjResp</c> 结构对应。
/// </summary>
public class Obj
{
    /// <summary>条目名称（文件或目录名）。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>文件大小（字节），目录通常为 0。</summary>
    public long Size { get; set; }
    /// <summary>是否为目录。</summary>
    [JsonPropertyName("is_dir")]
    public bool IsDir { get; set; }
    /// <summary>最后修改时间。</summary>
    public DateTimeOffset Modified { get; set; }
    /// <summary>创建时间。</summary>
    public DateTimeOffset Created { get; set; }
    /// <summary>文件的签名链接（用于临时访问）。</summary>
    public string? Sign { get; set; }
    /// <summary>缩略图地址。</summary>
    public string? Thumb { get; set; }
    /// <summary>文件类型编码（未知=0、文件夹=1、视频=2、音频=3、文本=4、图片=5）。</summary>
    public int Type { get; set; }
    /// <summary>哈希信息的字符串表示（如 "md5:xxx"），保留给前端兼容。</summary>
    [JsonPropertyName("hashinfo")]
    public string? HashInfo { get; set; }
    /// <summary>结构化哈希信息对象。</summary>
    [JsonPropertyName("hash_info")]
    public HashInfo? HashInfoObj { get; set; }
}

/// <summary>
/// 文件的哈希信息集合，包含可选的 MD5/SHA1/SHA256 校验值。
/// </summary>
public sealed class HashInfo
{
    /// <summary>MD5 值（可为空）。</summary>
    public string? Md5 { get; set; }
    /// <summary>SHA1 值（可为空）。</summary>
    public string? Sha1 { get; set; }
    /// <summary>SHA256 值（可为空）。</summary>
    public string? Sha256 { get; set; }

    /// <summary>
    /// 将优先可用的哈希格式化为 "alg:value" 字符串；若三者皆空则返回空字符串。
    /// </summary>
    /// <returns>哈希字符串或空字符串。</returns>
    public string ToHashInfoString() => Md5 is not null ? $"md5:{Md5}" : Sha1 is not null ? $"sha1:{Sha1}" : Sha256 is not null ? $"sha256:{Sha256}" : string.Empty;
}

/// <summary>
/// 表示某条路径引用，封装 <see cref="UPath"/> 及其常用便利属性。
/// </summary>
public sealed class ObjPath
{
    /// <summary>底层 VFS 路径。</summary>
    public UPath Path { get; set; }
    /// <summary>路径的末级名称（文件名或目录名）。</summary>
    public string Name => Path.GetName();
    /// <summary>是否为根路径。</summary>
    public bool IsRoot => Path == UPath.Root;
}
