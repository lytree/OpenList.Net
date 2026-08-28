using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace OpenList.Core.Models;

/// <summary>
/// 持久化搜索索引中的单行记录。与 OpenList Go 的数据库搜索索引（internal/search）对应。
/// 由 <c>SearchService</c> 在构建/更新索引的过程中写入，当 <c>search_index</c> 设置为
/// <c>"database"</c> 时由 <c>SearchAsync</c> 查询使用。
/// </summary>
public sealed class SearchIndexEntry
{
    /// <summary>索引记录主键。</summary>
    public long Id { get; set; }
    /// <summary>父目录（标准化绝对路径，例如 "/docs"）。</summary>
    public string Parent { get; set; } = string.Empty;
    /// <summary>条目名称（文件或目录）。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>是否为目录。</summary>
    [JsonPropertyName("is_dir")]
    public bool IsDir { get; set; }
    /// <summary>文件大小（字节）。</summary>
    public long Size { get; set; }
    /// <summary>完整路径 = Parent + "/" + Name。用于精确查找。</summary>
    public string Path { get; set; } = string.Empty;
    /// <summary>文件类型编码（未知=0、文件夹=1、视频=2、音频=3、文本=4、图片=5）。</summary>
    public int Type { get; set; }
}
