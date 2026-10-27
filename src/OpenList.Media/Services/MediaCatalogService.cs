using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

using OpenList.Media.Domain;
using OpenList.Media.Domain.Abstractions;

namespace OpenList.Media.Services;

/// <summary>
/// 媒体目录聚合查询服务的实现。所有口径统一走三步：先按库可见性筛、再按条目路径筛，
/// 最后才做分组与统计——否则普通用户会在题材计数里看到自己无权访问的条目。
/// </summary>
public sealed class MediaCatalogService : IMediaCatalogService
{
    /// <summary>评分分布的分段下界（左闭右开；最后一段到 10）。</summary>
    private static readonly double[] RatingEdges = [0, 5, 6, 7, 8, 9];

    private readonly OpenListDbContext _db;
    private readonly IMediaLibraryService _libraries;

    /// <summary>创建聚合查询服务实例。</summary>
    /// <param name="db">EF Core 数据库上下文。</param>
    /// <param name="libraries">媒体库服务：复用其库可见性判定，保证与条目列表同一口径。</param>
    public MediaCatalogService(OpenListDbContext db, IMediaLibraryService libraries)
    {
        _db = db;
        _libraries = libraries;
    }

    /// <inheritdoc />
    public async Task<Resp<MediaFacets>> GetFacetsAsync(long? libraryId, User user, CancellationToken ct = default)
    {
        var items = await LoadVisibleItemsAsync(libraryId, user, ct);
        var genres = items
            .SelectMany(i => i.Genres)
            .Select(g => g.Trim())
            .Where(g => g.Length > 0)
            .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MediaFacetCount { Value = g.Key, Count = g.Count() })
            .OrderByDescending(f => f.Count)
            .ThenBy(f => f.Value, StringComparer.CurrentCulture)
            .ToList();
        var years = items
            .Where(i => i.Year is not null)
            .GroupBy(i => i.Year!.Value)
            .OrderByDescending(g => g.Key)
            .Select(g => new MediaFacetCount
            {
                Value = g.Key.ToString(CultureInfo.InvariantCulture),
                Count = g.Count(),
            })
            .ToList();

        return Resp<MediaFacets>.Ok(new MediaFacets { Total = items.Count, Genres = genres, Years = years });
    }

    /// <inheritdoc />
    public async Task<Resp<PageResp<MediaPersonSummary>>> ListPersonsAsync(
        string? keyword, string? role, int page, int perPage, User user, CancellationToken ct = default)
    {
        var persons = await _db.MediaPersons.AsNoTracking().ToListAsync(ct);
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim();
            persons = persons.Where(p => p.Name.Contains(kw, StringComparison.CurrentCultureIgnoreCase)).ToList();
        }
        if (persons.Count == 0)
            return Resp<PageResp<MediaPersonSummary>>.Ok(new PageResp<MediaPersonSummary>());

        // 只统计当前用户可见的条目：条目 id 集合在内存里，关联表按它筛。
        var visibleIds = await LoadVisibleItemIdsAsync(null, user, ct);
        var credits = await _db.MediaItemPersons.AsNoTracking()
            .Where(r => visibleIds.Contains(r.ItemId))
            .Select(r => new { r.PersonId, r.ItemId, r.Role })
            .ToListAsync(ct);

        var roleFilter = role?.Trim();
        var aggregate = (from c in credits
                         where string.IsNullOrEmpty(roleFilter)
                               || string.Equals(c.Role, roleFilter, StringComparison.OrdinalIgnoreCase)
                         group c by c.PersonId into g
                         select new
                         {
                             PersonId = g.Key,
                             ItemCount = g.Select(x => x.ItemId).Distinct().Count(),
                             Roles = g.Select(x => x.Role).Distinct().ToList(),
                         }).ToDictionary(x => x.PersonId);

        var rows = persons
            .Where(p => aggregate.ContainsKey(p.Id))
            .Select(p => new MediaPersonSummary
            {
                Id = p.Id,
                Name = p.Name,
                TmdbId = p.TmdbId,
                ProfilePath = p.ProfilePath,
                ItemCount = aggregate[p.Id].ItemCount,
                Roles = aggregate[p.Id].Roles,
            })
            .OrderByDescending(p => p.ItemCount)
            .ThenBy(p => p.Name, StringComparer.CurrentCulture)
            .ToList();

        var p1 = Math.Max(1, page);
        var size = Math.Clamp(perPage, 1, 200);
        return Resp<PageResp<MediaPersonSummary>>.Ok(new PageResp<MediaPersonSummary>
        {
            Content = rows.Skip((p1 - 1) * size).Take(size).ToList(),
            Total = rows.Count,
        });
    }

    /// <inheritdoc />
    public async Task<Resp<PageResp<MediaItem>>> ListPersonItemsAsync(
        long personId, int page, int perPage, User user, CancellationToken ct = default)
    {
        if (!await _db.MediaPersons.AsNoTracking().AnyAsync(p => p.Id == personId, ct))
            return Resp<PageResp<MediaItem>>.Fail("person not found", RespCodes.NotFound);

        var itemIds = await _db.MediaItemPersons.AsNoTracking()
            .Where(r => r.PersonId == personId)
            .Select(r => r.ItemId)
            .Distinct()
            .ToListAsync(ct);
        if (itemIds.Count == 0)
            return Resp<PageResp<MediaItem>>.Ok(new PageResp<MediaItem>());

        var items = await LoadVisibleItemsAsync(null, user, ct);
        var idSet = itemIds.ToHashSet();
        var rows = items
            .Where(i => idSet.Contains(i.Id))
            .OrderByDescending(i => i.Year ?? 0)
            .ThenBy(i => i.Title, StringComparer.CurrentCulture)
            .ToList();

        var p1 = Math.Max(1, page);
        var size = Math.Clamp(perPage, 1, 200);
        return Resp<PageResp<MediaItem>>.Ok(new PageResp<MediaItem>
        {
            Content = rows.Skip((p1 - 1) * size).Take(size).ToList(),
            Total = rows.Count,
        });
    }

    /// <inheritdoc />
    public async Task<Resp<MediaStats>> GetStatsAsync(long? libraryId, User user, CancellationToken ct = default)
    {
        var items = await LoadVisibleItemsAsync(libraryId, user, ct);
        var idSet = items.Select(i => i.Id).ToHashSet();

        var files = await _db.MediaFiles.AsNoTracking()
            .Where(f => idSet.Contains(f.ItemId))
            .Select(f => new { f.Size, f.DurationSeconds })
            .ToListAsync(ct);

        var states = await _db.UserMediaStates.AsNoTracking()
            .Where(s => s.UserId == user.Id && idSet.Contains(s.ItemId))
            .Select(s => new { s.Favorite, s.Watched })
            .ToListAsync(ct);

        var byYear = items
            .Where(i => i.Year is not null)
            .GroupBy(i => i.Year!.Value)
            .OrderByDescending(g => g.Key)
            .Select(g => new MediaFacetCount
            {
                Value = g.Key.ToString(CultureInfo.InvariantCulture),
                Count = g.Count(),
            })
            .ToList();

        var byGenre = items
            .SelectMany(i => i.Genres)
            .Select(g => g.Trim())
            .Where(g => g.Length > 0)
            .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MediaFacetCount { Value = g.Key, Count = g.Count() })
            .OrderByDescending(f => f.Count)
            .ThenBy(f => f.Value, StringComparer.CurrentCulture)
            .ToList();

        var ratings = RatingEdges
            .Select((edge, index) =>
            {
                var upper = index + 1 < RatingEdges.Length ? RatingEdges[index + 1] : 10.0001;
                return new MediaRatingBucket
                {
                    Label = upper > 10
                        ? $"{(int)edge}.0-10"
                        : $"{edge:0.0}-{upper:0.0}",
                    Count = items.Count(i => i.Rating is not null && i.Rating >= edge && i.Rating < upper),
                };
            })
            .ToList();

        return Resp<MediaStats>.Ok(new MediaStats
        {
            LibraryCount = (await LoadVisibleLibrariesAsync(user, ct)).Count,
            ItemCount = items.Count,
            FileCount = files.Count,
            TotalBytes = files.Sum(f => f.Size),
            TotalDurationSeconds = files.Sum(f => (long)(f.DurationSeconds ?? 0)),
            WatchedCount = states.Count(s => s.Watched),
            FavoriteCount = states.Count(s => s.Favorite),
            ByYear = byYear,
            ByGenre = byGenre,
            Ratings = ratings,
        });
    }

    /// <inheritdoc />
    public async Task<Resp<MediaHome>> GetHomeAsync(int limit, User user, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, 60);
        var items = await LoadVisibleItemsAsync(null, user, ct);
        var byId = items.ToDictionary(i => i.Id);

        var recentlyAdded = items
            .OrderByDescending(i => i.CreatedAt)
            .ThenByDescending(i => i.Id)
            .Take(take)
            .ToList();

        var states = await _db.UserMediaStates.AsNoTracking()
            .Where(s => s.UserId == user.Id)
            .ToListAsync(ct);

        var continueWatching = states
            .Where(s => s.ProgressSeconds > 0 && !s.Watched && byId.ContainsKey(s.ItemId))
            .OrderByDescending(s => s.LastPlayedAt ?? s.UpdatedAt)
            .Take(take)
            .Select(s => byId[s.ItemId])
            .ToList();

        var recentlyFavorited = states
            .Where(s => s.Favorite && byId.ContainsKey(s.ItemId))
            .OrderByDescending(s => s.UpdatedAt)
            .Take(take)
            .Select(s => byId[s.ItemId])
            .ToList();

        return Resp<MediaHome>.Ok(new MediaHome
        {
            RecentlyAdded = recentlyAdded,
            ContinueWatching = continueWatching,
            RecentlyFavorited = recentlyFavorited,
        });
    }

    /// <inheritdoc />
    public async Task<Resp<PageResp<MediaTaxonomySummary>>> ListStudiosAsync(
        string? keyword, int page, int perPage, User user, CancellationToken ct = default)
        => await ListTaxonomyAsync(_db.MediaStudios, i => i.StudioId, keyword, page, perPage, user, ct);

    /// <inheritdoc />
    public async Task<Resp<MediaTaxonomySummary>> GetStudioAsync(
        long studioId, User user, CancellationToken ct = default)
        => await GetTaxonomyAsync(_db.MediaStudios, i => i.StudioId, studioId, "studio", user, ct);

    /// <inheritdoc />
    public async Task<Resp<PageResp<MediaItem>>> ListStudioItemsAsync(
        long studioId, int page, int perPage, User user, CancellationToken ct = default)
        => await ListTaxonomyItemsAsync(i => i.StudioId == studioId, studioId, "studio", page, perPage, user, ct);

    /// <inheritdoc />
    public async Task<Resp<PageResp<MediaTaxonomySummary>>> ListSeriesAsync(
        string? keyword, int page, int perPage, User user, CancellationToken ct = default)
        => await ListTaxonomyAsync(_db.MediaSeries, i => i.SeriesId, keyword, page, perPage, user, ct);

    /// <inheritdoc />
    public async Task<Resp<MediaTaxonomySummary>> GetSeriesAsync(
        long seriesId, User user, CancellationToken ct = default)
        => await GetTaxonomyAsync(_db.MediaSeries, i => i.SeriesId, seriesId, "series", user, ct);

    /// <inheritdoc />
    public async Task<Resp<PageResp<MediaItem>>> ListSeriesItemsAsync(
        long seriesId, int page, int perPage, User user, CancellationToken ct = default)
        => await ListTaxonomyItemsAsync(i => i.SeriesId == seriesId, seriesId, "series", page, perPage, user, ct);

    /// <summary>
    /// 单条片商/系列的通用实现。
    /// <para>
    /// 为什么需要它：详情页原先只能靠列表页跳转时塞进 URL 的 <c>?name=</c> 显示标题，
    /// 直接输地址（或刷新后从历史记录回来）就退化成「片商 #3」。补上这个查询后，
    /// 页面标题不再依赖来源，条目数也顺手由后端给（前端不用再从分页的 <c>total</c> 猜）。
    /// </para>
    /// <para>
    /// <c>item_count</c> 与列表口径一致：只数当前用户可见的条目。因此「存在但名下没有
    /// 可见条目」的维度行会返回 <c>count = 0</c> —— 这是对的（行本身是真实存在的），
    /// 与列表接口「无关联行不返回」的取舍不同：那里是推荐列表，这里是直接访问。
    /// </para>
    /// </summary>
    private async Task<Resp<MediaTaxonomySummary>> GetTaxonomyAsync(
        IQueryable<IMediaTaxonomyEntity> set,
        Func<MediaItem, long?> keyOf,
        long id, string kind, User user, CancellationToken ct)
    {
        var row = await set.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return Resp<MediaTaxonomySummary>.Fail($"{kind} not found", RespCodes.NotFound);

        var items = await LoadVisibleItemsAsync(null, user, ct);
        return Resp<MediaTaxonomySummary>.Ok(new MediaTaxonomySummary
        {
            Id = row.Id,
            Name = row.Name,
            ItemCount = items.Count(i => keyOf(i) == id),
        });
    }

    /// <summary>
    /// 片商/系列列表的通用实现：与 <see cref="ListPersonsAsync"/> 同口径 ——
    /// 只统计当前用户可见条目的关联计数，无关联的维度行不返回。
    /// </summary>
    private async Task<Resp<PageResp<MediaTaxonomySummary>>> ListTaxonomyAsync(
        IQueryable<IMediaTaxonomyEntity> set,
        Func<MediaItem, long?> keyOf,
        string? keyword, int page, int perPage, User user, CancellationToken ct)
    {
        var rows = await set.AsNoTracking().ToListAsync(ct);
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim();
            rows = rows.Where(r => r.Name.Contains(kw, StringComparison.CurrentCultureIgnoreCase)).ToList();
        }
        if (rows.Count == 0)
            return Resp<PageResp<MediaTaxonomySummary>>.Ok(new PageResp<MediaTaxonomySummary>());

        var items = await LoadVisibleItemsAsync(null, user, ct);
        var counts = items
            .Select(keyOf)
            .Where(id => id is not null)
            .GroupBy(id => id!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var summaries = rows
            .Where(r => counts.ContainsKey(r.Id))
            .Select(r => new MediaTaxonomySummary { Id = r.Id, Name = r.Name, ItemCount = counts[r.Id] })
            .OrderByDescending(s => s.ItemCount)
            .ThenBy(s => s.Name, StringComparer.CurrentCulture)
            .ToList();

        var p1 = Math.Max(1, page);
        var size = Math.Clamp(perPage, 1, 200);
        return Resp<PageResp<MediaTaxonomySummary>>.Ok(new PageResp<MediaTaxonomySummary>
        {
            Content = summaries.Skip((p1 - 1) * size).Take(size).ToList(),
            Total = summaries.Count,
        });
    }

    /// <summary>片商/系列名下条目列表的通用实现（口径同 ListPersonItemsAsync）。</summary>
    private async Task<Resp<PageResp<MediaItem>>> ListTaxonomyItemsAsync(
        Func<MediaItem, bool> predicate, long id, string kind,
        int page, int perPage, User user, CancellationToken ct)
    {
        var exists = kind == "studio"
            ? await _db.MediaStudios.AsNoTracking().AnyAsync(s => s.Id == id, ct)
            : await _db.MediaSeries.AsNoTracking().AnyAsync(s => s.Id == id, ct);
        if (!exists) return Resp<PageResp<MediaItem>>.Fail($"{kind} not found", RespCodes.NotFound);

        var items = await LoadVisibleItemsAsync(null, user, ct);
        var rows = items
            .Where(predicate)
            .OrderByDescending(i => i.Year ?? 0)
            .ThenBy(i => i.Title, StringComparer.CurrentCulture)
            .ToList();

        var p1 = Math.Max(1, page);
        var size = Math.Clamp(perPage, 1, 200);
        return Resp<PageResp<MediaItem>>.Ok(new PageResp<MediaItem>
        {
            Content = rows.Skip((p1 - 1) * size).Take(size).ToList(),
            Total = rows.Count,
        });
    }

    /// <summary>加载当前用户可见的条目（可限定单个库）。</summary>
    private async Task<List<MediaItem>> LoadVisibleItemsAsync(long? libraryId, User user, CancellationToken ct)
    {
        var query = _db.MediaItems.AsNoTracking();
        if (libraryId is not null) query = query.Where(i => i.LibraryId == libraryId);
        var items = await query.ToListAsync(ct);

        var visibleLibIds = (await LoadVisibleLibrariesAsync(user, ct)).Select(l => l.Id).ToHashSet();
        return items
            .Where(i => visibleLibIds.Contains(i.LibraryId))
            .Where(i => MediaLibraryService.IsPathVisible(user, i.DirectoryPath))
            .ToList();
    }

    /// <summary>加载当前用户可见的条目 id 集合（含库可见性与路径过滤）。</summary>
    private async Task<HashSet<long>> LoadVisibleItemIdsAsync(long? libraryId, User user, CancellationToken ct)
    {
        var rows = await _db.MediaItems.AsNoTracking()
            .Select(i => new { i.Id, i.LibraryId, i.DirectoryPath })
            .ToListAsync(ct);
        var visibleLibIds = (await LoadVisibleLibrariesAsync(user, ct)).Select(l => l.Id).ToHashSet();
        return rows
            .Where(r => libraryId is null || r.LibraryId == libraryId)
            .Where(r => visibleLibIds.Contains(r.LibraryId))
            .Where(r => MediaLibraryService.IsPathVisible(user, r.DirectoryPath))
            .Select(r => r.Id)
            .ToHashSet();
    }

    /// <summary>当前用户可见的媒体库。</summary>
    private async Task<List<MediaLibrary>> LoadVisibleLibrariesAsync(User user, CancellationToken ct)
    {
        var resp = await _libraries.ListAsync(user, ct);
        return resp.Data ?? [];
    }
}
