using Microsoft.EntityFrameworkCore;
using OpenList.Infrastructure.Data;
using OpenList.Media.Domain;

namespace OpenList.Media.Services;

/// <summary>
/// 片商/系列的「取或建」助手：按名称在 <see cref="MediaStudio"/> / <see cref="MediaSeries"/>
/// 里找现有行，找不到就建。NFO 扫描（MediaLibraryScanner）与 code provider 刮削
/// （MediaMetadataService）共用，保证两个来源聚合到同一维度行。
/// <para>
/// 与 <see cref="MediaPersonSync"/> 不同，这里每次只解析单个名称且必须立即拿到主键，
/// 所以找到/建好就保存。名称上的唯一索引是并发兜底：极端竞态下重复插入会抛异常由
/// 调用方整批重试，不做逐条捕获（维度行创建是低频操作）。
/// </para>
/// </summary>
internal static class MediaTaxonomySync
{
    /// <summary>按名称取或建片商，返回其主键。名称会去首尾空白。</summary>
    public static async Task<long> ResolveStudioAsync(OpenListDbContext db, DateTimeOffset now, string name, CancellationToken ct)
    {
        var clean = name.Trim();
        var existing = await db.MediaStudios.FirstOrDefaultAsync(s => s.Name == clean, ct);
        if (existing is not null) return existing.Id;

        var entity = new MediaStudio { Name = clean, CreatedAt = now };
        db.MediaStudios.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity.Id;
    }

    /// <summary>按名称取或建系列，返回其主键。名称会去首尾空白。</summary>
    public static async Task<long> ResolveSeriesAsync(OpenListDbContext db, DateTimeOffset now, string name, CancellationToken ct)
    {
        var clean = name.Trim();
        var existing = await db.MediaSeries.FirstOrDefaultAsync(s => s.Name == clean, ct);
        if (existing is not null) return existing.Id;

        var entity = new MediaSeries { Name = clean, CreatedAt = now };
        db.MediaSeries.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity.Id;
    }
}
