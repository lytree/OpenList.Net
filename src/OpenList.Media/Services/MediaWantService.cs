using Microsoft.EntityFrameworkCore;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Media.Domain;
using OpenList.Media.Domain.Abstractions;

namespace OpenList.Media.Services;

/// <summary>
/// 想看服务实现（见 <see cref="IMediaWantService"/>）。
/// <para>
/// 三个口径写在这里，别在端点里再判一次：
/// <list type="number">
/// <item>写操作要求已登录（游客 401），清单不属于媒体公开门面；</item>
/// <item>加入是幂等的 —— 唯一索引是 (MediaType, ExternalId)，并发重复点「想看」不会撞库；</item>
/// <item>入库回填只在「库里确实有这部剧/这部电影」时发生，且按类型匹配，
/// 避免 movie 与 tv 在 TMDB 里撞号时把两者对错。</item>
/// </list>
/// </para>
/// </summary>
public sealed class MediaWantService : IMediaWantService
{
    /// <summary>标题/简介的长度上限（与实体列宽一致，超了先截断再写）。</summary>
    private const int TitleMaxLength = 512;
    private const int OverviewMaxLength = 2000;

    private readonly OpenListDbContext _db;
    private readonly TimeProvider _time;

    /// <summary>创建想看服务实例。</summary>
    public MediaWantService(OpenListDbContext db, TimeProvider? time = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<Resp<MediaWant>> AddAsync(AddMediaWantReq req, User user, CancellationToken ct = default)
    {
        if (!user.IsAdmin()) return Resp<MediaWant>.Fail("permission denied", RespCodes.Forbidden);

        var externalId = (req.ExternalId ?? string.Empty).Trim();
        if (externalId.Length == 0)
            return Resp<MediaWant>.Fail("external_id is required", RespCodes.BadRequest);

        var mediaType = MediaExternal.NormalizeType(req.MediaType);
        var title = Trim(req.Title, TitleMaxLength);
        if (title.Length == 0)
            return Resp<MediaWant>.Fail("title is required", RespCodes.BadRequest);

        var want = await _db.MediaWants.FirstOrDefaultAsync(
            w => w.MediaType == mediaType && w.ExternalId == externalId, ct);

        if (want is null)
        {
            want = new MediaWant
            {
                MediaType = mediaType,
                ExternalId = externalId,
                Title = title,
                Year = req.Year,
                PosterPath = Blank(req.PosterPath),
                Overview = Trim(req.Overview, OverviewMaxLength),
                ReleaseDate = Blank(req.ReleaseDate),
                CreatedAt = _time.GetUtcNow(),
            };
            _db.MediaWants.Add(want);
        }
        else
        {
            // 幂等：已想看时只补空字段（更新流后来的标题/海报可能比第一次更全）。
            if (want.PosterPath is null or "") want.PosterPath = Blank(req.PosterPath);
            if (want.Overview is null or "") want.Overview = Trim(req.Overview, OverviewMaxLength);
            if (want.ReleaseDate is null or "") want.ReleaseDate = Blank(req.ReleaseDate);
            if (want.Year is null) want.Year = req.Year;
            if (want.Title.Length == 0) want.Title = title;
        }

        await _db.SaveChangesAsync(ct);

        // 已经在库里的作品直接标成「已入库」——「想看」页因此不会列它，
        // 也不会有人为了一部已经能播的片子去点「下载」。
        await LinkIngestedAsync(want.Id, ct);
        return Resp<MediaWant>.Ok(want);
    }

    /// <inheritdoc />
    public async Task<Resp<PageResp<MediaWant>>> ListAsync(
        int page, int perPage, bool includeIngested, User user, CancellationToken ct = default)
    {
        if (!user.IsAdmin()) return Resp<PageResp<MediaWant>>.Fail("permission denied", RespCodes.Forbidden);

        var q = _db.MediaWants.AsNoTracking();
        if (!includeIngested) q = q.Where(w => w.ItemId == null);

        var total = await q.LongCountAsync(ct);
        var p = Math.Max(1, page);
        var size = Math.Clamp(perPage, 1, 200);

        // Id 倒序 = 最近加入的在前。不用 CreatedAt 排序：SQLite 不支持 SQL 层
        // 按 DateTimeOffset 排序（见方案 §7 待补 6）。
        var content = await q.OrderByDescending(w => w.Id)
            .Skip((p - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        return Resp<PageResp<MediaWant>>.Ok(new PageResp<MediaWant> { Content = content, Total = total });
    }

    /// <inheritdoc />
    public async Task<Resp> RemoveAsync(long id, User user, CancellationToken ct = default)
    {
        if (!user.IsAdmin()) return Resp.Fail("permission denied", RespCodes.Forbidden);

        var want = await _db.MediaWants.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (want is null) return Resp.Ok();

        // 下载记录跟着走：它的存在只对「这条想看」有意义，留着就是孤儿行。
        var downloads = await _db.MediaDownloads.Where(d => d.WantId == id).ToListAsync(ct);
        _db.MediaDownloads.RemoveRange(downloads);
        _db.MediaWants.Remove(want);
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    /// <inheritdoc />
    public async Task<int> LinkIngestedAsync(long? wantId = null, CancellationToken ct = default)
    {
        var q = _db.MediaWants.Where(w => w.ItemId == null);
        if (wantId is not null) q = q.Where(w => w.Id == wantId.Value);
        var wants = await q.ToListAsync(ct);
        if (wants.Count == 0) return 0;

        var externalIds = wants.Select(w => w.ExternalId).Distinct().ToList();
        var items = await _db.MediaItems.AsNoTracking()
            .Where(i => i.TmdbId != null && externalIds.Contains(i.TmdbId))
            .Select(i => new ValueTuple<long, string, MediaLibraryType>(i.Id, i.TmdbId!, i.Type))
            .ToListAsync(ct);
        if (items.Count == 0) return 0;

        var linked = 0;
        foreach (var want in wants)
        {
            var expected = MediaExternal.ToLibraryType(want.MediaType);
            var hit = items.FirstOrDefault(i => i.Item2 == want.ExternalId && i.Item3 == expected);
            if (hit.Item1 == 0) continue;
            want.ItemId = hit.Item1;
            linked++;
        }
        if (linked > 0) await _db.SaveChangesAsync(ct);

        // 下载记录也顺手标上「已入库」：轮询任务据此停止复查。
        if (linked > 0)
        {
            var ids = wants.Where(w => w.ItemId != null).Select(w => w.Id).ToList();
            var downloads = await _db.MediaDownloads
                .Where(d => ids.Contains(d.WantId) && d.IngestedAt == null)
                .ToListAsync(ct);
            var now = _time.GetUtcNow();
            foreach (var d in downloads)
            {
                d.IngestedAt = now;
                d.ItemId ??= wants.First(w => w.Id == d.WantId).ItemId;
            }
            if (downloads.Count > 0) await _db.SaveChangesAsync(ct);
        }
        return linked;
    }

    private static string Trim(string? value, int max)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= max ? text : text[..max];
    }

    private static string? Blank(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length == 0 ? null : text.Length <= TitleMaxLength ? text : text[..TitleMaxLength];
    }
}
