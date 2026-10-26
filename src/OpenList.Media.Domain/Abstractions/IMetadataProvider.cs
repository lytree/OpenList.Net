using OpenList.Core.Models;

namespace OpenList.Media.Domain.Abstractions;

/// <summary>
/// 元数据源 provider（可插拔）。现有实现：TMDB（电影/剧集通用）、
/// code（番号域，按番号查自定义元数据 API）。后续可扩展豆瓣等。
/// provider 只负责"搜索与取详情"，写回 NFO/海报由 <see cref="IMediaMetadataService"/> 统一处理。
/// </summary>
public interface IMetadataProvider
{
    /// <summary>provider 标识（如 "tmdb"）。</summary>
    string Name { get; }
    /// <summary>provider 是否已配置可用（如 API Key 已设置）。</summary>
    Task<bool> IsConfiguredAsync(CancellationToken ct = default);
    /// <summary>按标题/年份搜索候选条目。</summary>
    Task<List<MetadataSearchResult>> SearchAsync(string title, int? year, MediaLibraryType type, string language, CancellationToken ct = default);
    /// <summary>按 provider 条目 id 取完整元数据（含创作者）。</summary>
    Task<MediaMetadata?> GetAsync(string providerId, MediaLibraryType type, string language, CancellationToken ct = default);
    /// <summary>下载图片（海报/背景图/头像）为字节流。返回 null 表示失败。</summary>
    Task<byte[]?> GetImageAsync(string imagePath, CancellationToken ct = default);
}

/// <summary>元数据搜索候选。</summary>
public sealed class MetadataSearchResult
{
    /// <summary>provider 条目 id。</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>标题。</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>原始标题。</summary>
    public string? OriginalTitle { get; set; }
    /// <summary>发行年份。</summary>
    public int? Year { get; set; }
    /// <summary>简介。</summary>
    public string? Overview { get; set; }
    /// <summary>海报路径（provider 内路径）。</summary>
    public string? PosterPath { get; set; }
    /// <summary>源站评分（0-10）。</summary>
    public double? Rating { get; set; }
}

/// <summary>完整媒体元数据。</summary>
public sealed class MediaMetadata
{
    /// <summary>provider 条目 id。</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>标题。</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>原始标题。</summary>
    public string? OriginalTitle { get; set; }
    /// <summary>发行年份。</summary>
    public int? Year { get; set; }
    /// <summary>简介。</summary>
    public string? Overview { get; set; }
    /// <summary>海报路径（provider 内路径）。</summary>
    public string? PosterPath { get; set; }
    /// <summary>背景图路径（provider 内路径）。</summary>
    public string? BackdropPath { get; set; }
    /// <summary>源站评分（0-10）。</summary>
    public double? Rating { get; set; }
    /// <summary>类型标签。</summary>
    public List<string> Genres { get; set; } = [];
    /// <summary>创作者列表。</summary>
    public List<MetadataPerson> Persons { get; set; } = [];
    /// <summary>
    /// 片商（制作商），可为空。目前只有 code provider 填写；
    /// TMDB 等其他 provider 留空，落库时不会动条目已有的片商关联。
    /// </summary>
    public string? Studio { get; set; }
    /// <summary>系列，可为空。填写规则同 <see cref="Studio"/>。</summary>
    public string? Series { get; set; }
}

/// <summary>元数据中的创作者。</summary>
public sealed class MetadataPerson
{
    /// <summary>姓名。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>provider person id。</summary>
    public string? ProviderId { get; set; }
    /// <summary>角色类型（director / actor / writer / creator）。</summary>
    public string Role { get; set; } = string.Empty;
    /// <summary>饰演角色名（演员）。</summary>
    public string? Character { get; set; }
    /// <summary>头像路径（provider 内路径）。</summary>
    public string? ProfilePath { get; set; }
    /// <summary>演员表排序。</summary>
    public int Order { get; set; }
}
