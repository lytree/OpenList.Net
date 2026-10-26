using Microsoft.EntityFrameworkCore;
using OpenList.Media.Domain;

namespace OpenList.Infrastructure.Data;

/// <summary>
/// 媒体域（<see cref="OpenList.Media"/>）的 EF Core 映射配置。
/// <para>
/// 从 <see cref="OpenListDbContext.OnModelCreating"/> 里抽出来的原因有两个：
/// 一是让上下文本身只保留「有哪些 DbSet」这一层信息，映射细节按领域归档；
/// 二是媒体域现在有独立的契约程序集（<c>OpenList.Media.Domain</c>），
/// 表名/索引/列宽这类持久化知识集中在 Infrastructure，避免领域实体被 EF 特性污染。
/// </para>
/// <para>
/// 表名沿用 <c>{prefix}media_*</c> 约定（与迁移 AddMediaSchema / AddMediaCollections 一致），
/// 表结构不因本次拆分发生任何变化。
/// </para>
/// </summary>
internal static class MediaModelConfiguration
{
    /// <summary>把媒体域的九个实体映射进模型。<paramref name="prefix"/> 为全局表前缀。</summary>
    public static void Apply(ModelBuilder b, string prefix)
    {
        b.Entity<MediaLibrary>(e =>
        {
            e.ToTable(prefix + "media_libraries");
            e.HasKey(l => l.Id);
            e.HasIndex(l => l.Path).IsUnique();
        });
        b.Entity<MediaScanLog>(e =>
        {
            e.ToTable(prefix + "media_scan_logs");
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.LibraryId);
        });
        b.Entity<MediaItem>(e =>
        {
            e.ToTable(prefix + "media_items");
            e.HasKey(i => i.Id);
            e.HasIndex(i => i.LibraryId);
            e.HasIndex(i => new { i.LibraryId, i.DirectoryPath }).IsUnique();
            e.HasIndex(i => i.TmdbId);
            // 番号域：番号是主检索键，片商/系列是聚合维度（各建普通索引）。
            e.HasIndex(i => i.Code);
            e.HasIndex(i => i.StudioId);
            e.HasIndex(i => i.SeriesId);
            // 元数据来源（local/tmdb）有按值筛选的查询路径（批量刮削只挑 Pending 的库来源条目），列宽取最小够用。
            e.Property(i => i.MetadataProvider).HasMaxLength(32);
            // 番号形如 "MIDE-883"，64 足够容纳 FC2 等长编号。
            e.Property(i => i.Code).HasMaxLength(64);
            // 题材是「列在库、集合在内存」：GenresJson 是唯一映射列，
            // 实体上的 Genres 是它的强类型只读视图（NotMapped）。
            e.Ignore(i => i.Genres);
        });
        b.Entity<MediaFile>(e =>
        {
            e.ToTable(prefix + "media_files");
            e.HasKey(f => f.Id);
            e.HasIndex(f => f.ItemId);
            e.HasIndex(f => f.Path).IsUnique();
        });
        b.Entity<MediaPerson>(e =>
        {
            e.ToTable(prefix + "media_persons");
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.Name);
            e.HasIndex(p => new { p.Name, p.TmdbId }).IsUnique();
        });
        b.Entity<MediaItemPerson>(e =>
        {
            e.ToTable(prefix + "media_item_persons");
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.ItemId);
            e.HasIndex(r => r.PersonId);
            e.HasIndex(r => new { r.ItemId, r.PersonId, r.Role }).IsUnique();
        });
        b.Entity<UserMediaState>(e =>
        {
            e.ToTable(prefix + "user_media_states");
            e.HasKey(s => s.Id);
            e.HasIndex(s => new { s.UserId, s.ItemId }).IsUnique();
        });
        b.Entity<MediaPlaylist>(e =>
        {
            e.ToTable(prefix + "media_playlists");
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.UserId);
        });
        b.Entity<MediaPlaylistItem>(e =>
        {
            e.ToTable(prefix + "media_playlist_items");
            e.HasKey(i => i.Id);
            e.HasIndex(i => i.PlaylistId);
            // 同一个条目在一个列表里只允许出现一次；位置唯一保证重排不会撞号。
            e.HasIndex(i => new { i.PlaylistId, i.ItemId }).IsUnique();
            e.HasIndex(i => new { i.PlaylistId, i.Position }).IsUnique();
        });
        b.Entity<MediaStudio>(e =>
        {
            e.ToTable(prefix + "media_studios");
            e.HasKey(s => s.Id);
            // 名称即自然键：扫描/刮削按名取或建，唯一索引兜底防并发重复。
            e.HasIndex(s => s.Name).IsUnique();
            e.Property(s => s.Name).HasMaxLength(256);
        });
        b.Entity<MediaSeries>(e =>
        {
            e.ToTable(prefix + "media_series");
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.Name).IsUnique();
            e.Property(s => s.Name).HasMaxLength(256);
        });
    }
}
