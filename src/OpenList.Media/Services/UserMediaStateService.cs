using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

using OpenList.Media.Domain;
using OpenList.Media.Domain.Abstractions;

namespace OpenList.Media.Services;

/// <summary>
/// 用户媒体状态服务：收藏/看过/评分/播放进度的读写，以及口味画像统计。
/// 状态按 (UserId, ItemId) 隔离，多用户互不影响；条目与元数据全局共享。
/// </summary>
public sealed class UserMediaStateService : IUserMediaStateService
{
    private readonly OpenListDbContext _db;
    private readonly IMediaLibraryService _libraries;
    private readonly TimeProvider _time;

    /// <summary>创建用户媒体状态服务实例。</summary>
    public UserMediaStateService(OpenListDbContext db, IMediaLibraryService libraries, TimeProvider? time = null)
    {
        _db = db;
        _libraries = libraries;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<Resp<UserMediaState>> GetAsync(long itemId, User user, CancellationToken ct = default)
    {
        var item = await _db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return Resp<UserMediaState>.Fail("item not found", RespCodes.NotFound);
        if (!_libraries.CanSeeItem(user, item)) return Resp<UserMediaState>.Fail("permission denied", 403);

        var state = await _db.UserMediaStates.AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == user.Id && s.ItemId == itemId, ct);
        return Resp<UserMediaState>.Ok(state ?? new UserMediaState { UserId = user.Id, ItemId = itemId });
    }

    /// <inheritdoc />
    public async Task<Resp<UserMediaState>> UpdateAsync(long itemId, UpdateUserMediaStateReq req, User user, CancellationToken ct = default)
    {
        var item = await _db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return Resp<UserMediaState>.Fail("item not found", RespCodes.NotFound);
        if (!_libraries.CanSeeItem(user, item)) return Resp<UserMediaState>.Fail("permission denied", 403);

        if (req.UserRating is not null && (req.UserRating < 1 || req.UserRating > 10))
            return Resp<UserMediaState>.Fail("user_rating must be between 1 and 10", RespCodes.BadRequest);
        if (req.ProgressSeconds is < 0)
            return Resp<UserMediaState>.Fail("progress_seconds must be non-negative", RespCodes.BadRequest);

        var state = await _db.UserMediaStates.FirstOrDefaultAsync(s => s.UserId == user.Id && s.ItemId == itemId, ct);
        if (state is null)
        {
            state = new UserMediaState { UserId = user.Id, ItemId = itemId, UpdatedAt = _time.GetUtcNow() };
            _db.UserMediaStates.Add(state);
        }

        if (req.Favorite is not null) state.Favorite = req.Favorite.Value;
        if (req.Watched is not null) state.Watched = req.Watched.Value;
        if (req.WatchLater is not null) state.WatchLater = req.WatchLater.Value;
        if (req.Flagged is not null) state.Flagged = req.Flagged.Value;
        if (req.FlagNote is not null) state.FlagNote = req.FlagNote.Length == 0 ? null : req.FlagNote.Trim();
        if (req.UserRating is not null) state.UserRating = req.UserRating;
        if (req.ProgressSeconds is not null)
        {
            state.ProgressSeconds = req.ProgressSeconds.Value;
            state.LastPlayedAt = _time.GetUtcNow();
        }
        state.UpdatedAt = _time.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return Resp<UserMediaState>.Ok(state);
    }

    /// <inheritdoc />
    public async Task<Resp<PageResp<MediaItem>>> ListFavoritesAsync(int page, int perPage, User user, CancellationToken ct = default)
        => await ListByStateAsync(user, s => s.Favorite, page, perPage, ct);

    /// <inheritdoc />
    public async Task<Resp<PageResp<MediaItem>>> ListInProgressAsync(int page, int perPage, User user, CancellationToken ct = default)
        => await ListByStateAsync(user, s => s.ProgressSeconds > 0 && !s.Watched, page, perPage, ct);

    /// <inheritdoc />
    public async Task<Resp<PageResp<MediaItem>>> ListWatchLaterAsync(int page, int perPage, User user, CancellationToken ct = default)
        => await ListByStateAsync(user, s => s.WatchLater, page, perPage, ct);

    /// <inheritdoc />
    public async Task<Resp<PageResp<MediaItem>>> ListFlaggedAsync(int page, int perPage, User user, CancellationToken ct = default)
        => await ListByStateAsync(user, s => s.Flagged, page, perPage, ct);

    /// <inheritdoc />
    public async Task<Resp<PageResp<MediaItem>>> ListUnseenAsync(
        int page, int perPage, long? libraryId, User user, CancellationToken ct = default)
    {
        var q = _db.MediaItems.AsNoTracking();
        if (libraryId is not null) q = q.Where(i => i.LibraryId == libraryId.Value);
        var candidates = await q.ToListAsync(ct);

        // "未看"没有状态行可 join，只能先取出"看过"的 id 集合再排除。
        var watchedIds = await _db.UserMediaStates.AsNoTracking()
            .Where(s => s.UserId == user.Id && s.Watched)
            .Select(s => s.ItemId)
            .ToListAsync(ct);
        var watched = watchedIds.ToHashSet();

        // 新入库的排在前面——"未看"列表的阅读顺序是"最近加的还没看"。
        var unseen = candidates
            .Where(i => !watched.Contains(i.Id) && _libraries.CanSeeItem(user, i))
            .OrderByDescending(i => i.CreatedAt)
            .ThenBy(i => i.Title)
            .ToList();

        var p = Math.Max(1, page);
        var size = Math.Clamp(perPage, 1, 200);
        return Resp<PageResp<MediaItem>>.Ok(new PageResp<MediaItem>
        {
            Content = unseen.Skip((p - 1) * size).Take(size).ToList(),
            Total = unseen.Count,
        });
    }

    /// <inheritdoc />
    public async Task<Resp<TasteProfile>> GetTasteProfileAsync(User user, CancellationToken ct = default)
    {
        var rows = await (from s in _db.UserMediaStates.AsNoTracking()
                          join i in _db.MediaItems.AsNoTracking() on s.ItemId equals i.Id
                          where s.UserId == user.Id
                          select new { State = s, Item = i }).ToListAsync(ct);

        var visible = rows.Where(r => _libraries.CanSeeItem(user, r.Item)).ToList();
        var profile = new TasteProfile
        {
            FavoriteCount = visible.Count(r => r.State.Favorite),
            WatchedCount = visible.Count(r => r.State.Watched),
            TotalWatchSeconds = visible.Sum(r => r.State.ProgressSeconds),
        };

        // 类型权重：收藏 +2、看过 +1、高分（≥8）+2。
        var genreWeights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in visible)
        {
            var weight = (r.State.Favorite ? 2 : 0) + (r.State.Watched ? 1 : 0) + (r.State.UserRating >= 8 ? 2 : 0);
            if (weight == 0) continue;
            foreach (var g in r.Item.Genres)
                genreWeights[g] = genreWeights.GetValueOrDefault(g) + weight;
        }
        profile.Genres = genreWeights
            .OrderByDescending(kv => kv.Value)
            .Take(10)
            .Select(kv => new TasteGenreWeight { Genre = kv.Key, Weight = kv.Value })
            .ToList();

        // 创作者权重：同样按条目权重累计。
        var itemWeights = visible
            .Select(r => new
            {
                r.Item.Id,
                Weight = (r.State.Favorite ? 2 : 0) + (r.State.Watched ? 1 : 0) + (r.State.UserRating >= 8 ? 2 : 0),
            })
            .Where(x => x.Weight > 0)
            .ToDictionary(x => x.Id, x => x.Weight);

        if (itemWeights.Count > 0)
        {
            var ids = itemWeights.Keys.ToList();
            var credits = await (from c in _db.MediaItemPersons.AsNoTracking()
                                 join p in _db.MediaPersons.AsNoTracking() on c.PersonId equals p.Id
                                 where ids.Contains(c.ItemId)
                                 select new { c.ItemId, p.Id, p.Name, p.ProfilePath }).ToListAsync(ct);

            profile.Persons = credits
                .GroupBy(c => new { c.Id, c.Name, c.ProfilePath })
                .Select(g => new TastePersonWeight
                {
                    PersonId = g.Key.Id,
                    Name = g.Key.Name,
                    ProfilePath = g.Key.ProfilePath,
                    Weight = g.Sum(x => itemWeights.GetValueOrDefault(x.ItemId)),
                })
                .OrderByDescending(p => p.Weight)
                .Take(10)
                .ToList();
        }

        return Resp<TasteProfile>.Ok(profile);
    }

    /// <summary>按状态条件查询条目（分页 + 可见性过滤）。</summary>
    private async Task<Resp<PageResp<MediaItem>>> ListByStateAsync(
        User user, System.Linq.Expressions.Expression<Func<UserMediaState, bool>> predicate,
        int page, int perPage, CancellationToken ct)
    {
        // SQLite 不支持在 ORDER BY 中对 DateTimeOffset 生成表达式（EF 会抛
        // NotSupportedException），所以先把 (状态, 条目) 取回内存再按更新时间排序。
        // 单个用户的收藏/在看记录量级很小，客户端排序无性能顾虑。
        var rows = await (from s in _db.UserMediaStates.AsNoTracking().Where(predicate)
                          join i in _db.MediaItems.AsNoTracking() on s.ItemId equals i.Id
                          where s.UserId == user.Id
                          select new { State = s, Item = i }).ToListAsync(ct);

        var items = rows.OrderByDescending(r => r.State.UpdatedAt).Select(r => r.Item).ToList();

        var visible = items.Where(i => _libraries.CanSeeItem(user, i)).ToList();
        var p = Math.Max(1, page);
        var size = Math.Clamp(perPage, 1, 200);
        return Resp<PageResp<MediaItem>>.Ok(new PageResp<MediaItem>
        {
            Content = visible.Skip((p - 1) * size).Take(size).ToList(),
            Total = visible.Count,
        });
    }
}
