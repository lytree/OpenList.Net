namespace OpenList.Media.Domain;

/// <summary>
/// 片商/系列实体的公共形态：目录聚合查询（MediaCatalogService）据此泛型化，
/// 避免两套一模一样的列表代码。
/// </summary>
public interface IMediaTaxonomyEntity
{
    /// <summary>主键。</summary>
    long Id { get; }
    /// <summary>名称（自然键）。</summary>
    string Name { get; set; }
    /// <summary>创建时间。</summary>
    DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// 片商（制作商）维度，映射数据库 x_media_studios 表。
/// 番号域的聚合维度之一：多个条目可指向同一片商（MediaItem.StudioId）。
/// 行由扫描（NFO &lt;studio&gt;）与刮削（code provider 返回 studio 字段）按需创建，
/// 名称即自然键，全局共享。
/// </summary>
public sealed class MediaStudio : IMediaTaxonomyEntity
{
    /// <summary>片商主键。</summary>
    public long Id { get; set; }
    /// <summary>片商名称（去首尾空白后入库，全局唯一）。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// 系列维度，映射数据库 x_media_series 表。
/// 与片商同构：多个条目可属于同一系列（MediaItem.SeriesId），
/// 行由扫描（NFO &lt;series&gt;/&lt;set&gt;）与刮削按需创建。
/// </summary>
public sealed class MediaSeries : IMediaTaxonomyEntity
{
    /// <summary>系列主键。</summary>
    public long Id { get; set; }
    /// <summary>系列名称（去首尾空白后入库，全局唯一）。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
