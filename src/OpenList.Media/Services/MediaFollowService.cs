using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Media.Domain;
using OpenList.Media.Domain.Abstractions;
using OpenList.Media.Endpoints;
using OpenList.Media.Providers;

namespace OpenList.Media.Services;

/// <summary>
/// 关注创作者 → 新作品更新流（P1-3）。
/// <para>
/// 差集口径：每次检查向 <see cref="IPersonFeedProvider"/> 取该人物的**全部**作品，
/// <c>(PersonId, ExternalId)</c> 在 <c>x_media_follow_entries</c> 里没有的才插入，
/// <see cref="MediaFollowEntry.FirstSeenAt"/> 就是它进入更新流的时刻。因此首次关注时的检查
/// 会把现有作品全部落成条目 —— 对这位用户来说它们确实是「首次见到」。
/// </para>
/// <para>
/// <see cref="MediaFollowEntry.ItemId"/> 不落库，读取时按 <c>MediaItem.TmdbId</c> 对表得出；
/// 刮削随时可能把作品入库，落库就得多维护一条同步链。
/// </para>
/// </summary>
public sealed class MediaFollowService : IMediaFollowService
{
    /// <summary>人物间检查间隔（对齐批量刮削，避免触发 TMDB 频率限制）。</summary>
    public static readonly TimeSpan DefaultPersonDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>单次 IN 查询的 id 上限：SQLite 的参数上限是 999～32766，分块最稳。</summary>
    private const int ChunkSize = 500;

    /// <summary>写进 <see cref="MediaFollow.LastError"/> 的最大长度（异常消息可能很长）。</summary>
    private const int MaxErrorLength = 500;

    /// <summary>批量取关注状态时最多处理的人物数，超出部分按未关注返回。</summary>
    private const int MaxStateIds = 200;

    /// <summary>整轮检查的名额：宿主任务与手动「全部检查」不能同时跑。</summary>
    private static readonly SemaphoreSlim RunGate = new(1, 1);

    /// <summary>
    /// 单个人物的差集名额。<see cref="RunGate"/> 挡不住「整轮在跑时又手动检查同一个人」，
    /// 两条路径同时插同一个 (PersonId, ExternalId) 会撞唯一索引。
    /// </summary>
    private static readonly SemaphoreSlim PersonGate = new(1, 1);

    private readonly OpenListDbContext _db;
    private readonly IMediaLibraryService _libraries;
    private readonly IPersonFeedProvider _feedProvider;
    private readonly TimeProvider _time;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly ILogger<MediaFollowService>? _logger;
    private readonly TimeSpan _personDelay;

    /// <summary>创建关注服务实例。</summary>
    /// <param name="db">数据库上下文。</param>
    /// <param name="libraries">媒体库服务（私有库可见性判定）。</param>
    /// <param name="feedProvider">作品流数据源（TMDB）。</param>
    /// <param name="time">时间源。</param>
    /// <param name="scopeFactory">作用域工厂（手动「全部检查」转后台时开新作用域）。</param>
    /// <param name="logger">日志。</param>
    /// <param name="personDelay">整轮检查时两个人物之间的间隔。</param>
    public MediaFollowService(
        OpenListDbContext db,
        IMediaLibraryService libraries,
        IPersonFeedProvider feedProvider,
        TimeProvider? time = null,
        IServiceScopeFactory? scopeFactory = null,
        ILogger<MediaFollowService>? logger = null,
        TimeSpan? personDelay = null)
    {
        _db = db;
        _libraries = libraries;
        _feedProvider = feedProvider;
        _time = time ?? TimeProvider.System;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _personDelay = personDelay ?? DefaultPersonDelay;
    }

    /// <inheritdoc />
    public async Task<Resp<Dictionary<long, bool>>> GetStateAsync(
        IReadOnlyCollection<long> personIds, User user, CancellationToken ct = default)
    {
        var ids = personIds.Where(id => id > 0).Distinct().Take(MaxStateIds).ToList();
        var result = ids.ToDictionary(id => id, _ => false);
        if (ids.Count == 0) return Resp<Dictionary<long, bool>>.Ok(result);

        var followed = await _db.MediaFollows.AsNoTracking()
            .Where(f => ids.Contains(f.PersonId))
            .Select(f => f.PersonId)
            .ToListAsync(ct);
        if (followed.Count == 0) return Resp<Dictionary<long, bool>>.Ok(result);

        // 私有库里的人物对游客按「不存在」处理：关注了也不说，免得借这个接口确认其存在。
        var visible = await ListVisiblePersonIdsAsync(user, followed, ct);
        foreach (var id in followed)
        {
            if (visible.Contains(id)) result[id] = true;
        }
        return Resp<Dictionary<long, bool>>.Ok(result);
    }

    /// <inheritdoc />
    public async Task<Resp<MediaFollow>> FollowAsync(long personId, User user, CancellationToken ct = default)
    {
        if (!user.IsAdmin()) return Resp<MediaFollow>.Fail("permission denied", RespCodes.Forbidden);

        var person = await _db.MediaPersons.AsNoTracking().FirstOrDefaultAsync(p => p.Id == personId, ct);
        if (person is null) return Resp<MediaFollow>.Fail("person not found", RespCodes.NotFound);
        // 没有 TMDB id（本地 NFO 的人物）就没有作品流可查：关注了也永远不会更新，直接拒绝。
        if (string.IsNullOrWhiteSpace(person.TmdbId))
            return Resp<MediaFollow>.Fail("person has no tmdb id", RespCodes.BadRequest);

        var follow = await _db.MediaFollows.FirstOrDefaultAsync(f => f.PersonId == personId, ct);
        if (follow is null)
        {
            follow = new MediaFollow { PersonId = personId, CreatedAt = _time.GetUtcNow() };
            _db.MediaFollows.Add(follow);
            await _db.SaveChangesAsync(ct);
        }
        else if (follow.LastCheckedAt is null && follow.LastError is not null)
        {
            // 上次关注时的首检失败（多半是当时没配 API key）：再点一次关注就重试，
            // 不必让用户去找「检查更新」。
        }
        else
        {
            return Resp<MediaFollow>.Ok(follow);
        }

        // 首次关注立刻检查：把这个人现有的全部作品落成更新流条目。
        // 检查失败不算关注失败 —— 关注行已经建好，原因记在 LastError。
        await CheckFollowAsync(follow, ct);
        return Resp<MediaFollow>.Ok(follow);
    }

    /// <inheritdoc />
    public async Task<Resp> UnfollowAsync(long personId, User user, CancellationToken ct = default)
    {
        if (!user.IsAdmin()) return Resp.Fail("permission denied", RespCodes.Forbidden);

        var follow = await _db.MediaFollows.FirstOrDefaultAsync(f => f.PersonId == personId, ct);
        if (follow is null) return Resp.Ok();

        // 级联：取关后更新流里这个人的条目一并消失。
        var entries = await _db.MediaFollowEntries.Where(e => e.FollowId == follow.Id).ToListAsync(ct);
        _db.MediaFollowEntries.RemoveRange(entries);
        _db.MediaFollows.Remove(follow);
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    /// <inheritdoc />
    public async Task<Resp<List<FollowedPerson>>> ListFollowedAsync(User user, CancellationToken ct = default)
    {
        // 关注数量是个位到几十的量级，取回后在内存里排：SQLite 不支持在 SQL 里
        // 按 DateTimeOffset 排序（EF 会抛 "cannot order by expressions of type
        // 'DateTimeOffset'"，见方案 §7 待补 6）。
        var follows = (await _db.MediaFollows.AsNoTracking().ToListAsync(ct))
            .OrderBy(f => f.CreatedAt).ThenBy(f => f.Id)
            .ToList();
        if (follows.Count == 0) return Resp<List<FollowedPerson>>.Ok([]);

        var personIds = follows.Select(f => f.PersonId).ToList();
        var persons = await _db.MediaPersons.AsNoTracking()
            .Where(p => personIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);
        var visible = await ListVisiblePersonIdsAsync(user, personIds, ct);

        var entries = await _db.MediaFollowEntries.AsNoTracking()
            .Select(e => new { e.PersonId, e.MediaType, e.ExternalId })
            .ToListAsync(ct);
        var items = await LoadItemsByExternalAsync([.. entries.Select(e => e.ExternalId).Distinct()], user, ct);

        var entriesByPerson = entries.ToLookup(e => e.PersonId);
        var result = new List<FollowedPerson>(follows.Count);
        foreach (var follow in follows)
        {
            if (!persons.TryGetValue(follow.PersonId, out var person) || !visible.Contains(person.Id)) continue;
            result.Add(new FollowedPerson
            {
                PersonId = person.Id,
                Name = person.Name,
                ProfilePath = person.ProfilePath,
                TmdbId = person.TmdbId,
                CreatedAt = follow.CreatedAt,
                LastCheckedAt = follow.LastCheckedAt,
                LastError = follow.LastError,
                NewCount = entriesByPerson[person.Id].Count(e => FindItem(items, e.MediaType, e.ExternalId) is null),
            });
        }
        return Resp<List<FollowedPerson>>.Ok(result);
    }

    /// <inheritdoc />
    public async Task<Resp<PageResp<FollowFeedEntry>>> GetFeedAsync(
        int page, int perPage, long? personId, User user, CancellationToken ct = default)
    {
        IQueryable<MediaFollowEntry> q = _db.MediaFollowEntries.AsNoTracking();

        if (personId is null)
        {
            // 游客的整条更新流要先把私有库里的人物筛掉；已登录用户看全部。
            if (user.Id == 0)
            {
                var followedIds = await _db.MediaFollows.AsNoTracking().Select(f => f.PersonId).ToListAsync(ct);
                var visible = await ListVisiblePersonIdsAsync(user, followedIds, ct);
                q = q.Where(e => visible.Contains(e.PersonId));
            }
        }
        else
        {
            // 指定人物：不存在 / 对当前用户不可见一律 404（不给 403 那种存在性信号）。
            if (!await CanSeePersonAsync(user, personId.Value, ct))
                return Resp<PageResp<FollowFeedEntry>>.Fail("person not found", RespCodes.NotFound);
            q = q.Where(e => e.PersonId == personId.Value);
        }

        var total = await q.LongCountAsync(ct);
        var p = Math.Max(1, page);
        var size = Math.Clamp(perPage, 1, 200);
        // 为什么按 Id 而不是 FirstSeenAt 翻页：SQLite 不支持 SQL 层 DateTimeOffset 排序。
        // Id 随插入递增，而 FirstSeenAt 是「插入那一刻」，所以 Id 倒序 = 最新一批在前；
        // 为了让同一批内仍保持数据源的顺序（代表作在前），差集插入时是**倒序**写入的，
        // 于是 Id 倒序恰好还原成源顺序（见 CheckFollowAsync 的说明）。
        var rows = await q
            .OrderByDescending(e => e.Id)
            .Skip((p - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        var page_ = new PageResp<FollowFeedEntry> { Total = total };
        if (rows.Count == 0) return Resp<PageResp<FollowFeedEntry>>.Ok(page_);

        var personIds = rows.Select(e => e.PersonId).Distinct().ToList();
        var persons = await _db.MediaPersons.AsNoTracking()
            .Where(x => personIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var items = await LoadItemsByExternalAsync([.. rows.Select(e => e.ExternalId).Distinct()], user, ct);
        var wants = await LoadOpenWantsAsync([.. rows.Select(e => e.ExternalId).Distinct()], ct);

        foreach (var e in rows)
        {
            persons.TryGetValue(e.PersonId, out var person);
            var item = FindItem(items, e.MediaType, e.ExternalId);
            wants.TryGetValue((e.MediaType, e.ExternalId), out var want);
            page_.Content.Add(new FollowFeedEntry
            {
                Id = e.Id,
                PersonId = e.PersonId,
                PersonName = person?.Name ?? string.Empty,
                PersonProfilePath = person?.ProfilePath,
                MediaType = e.MediaType,
                ExternalId = e.ExternalId,
                Title = e.Title,
                ReleaseDate = e.ReleaseDate,
                PosterUrl = ToAbsoluteUrl(e.PosterPath),
                Role = e.Role,
                FirstSeenAt = e.FirstSeenAt,
                ItemId = item?.ItemId,
                WantId = want?.Id,
            });
        }
        return Resp<PageResp<FollowFeedEntry>>.Ok(page_);
    }

    /// <inheritdoc />
    public async Task<Resp<FollowCheckResult>> CheckAsync(long? personId, User user, CancellationToken ct = default)
    {
        if (!user.IsAdmin()) return Resp<FollowCheckResult>.Fail("permission denied", RespCodes.Forbidden);

        if (personId is null)
        {
            // 名额由 CheckAllAsync 自己抢：这里只判断「是不是已经有一轮在跑」，
            // 免得前端连点两次看到两个 Started=true 却只跑了一轮。
            if (IsRunInProgress)
                return Resp<FollowCheckResult>.Ok(new FollowCheckResult { Started = false });
            _ = Task.Run(() => RunBackgroundCheckAsync());
            return Resp<FollowCheckResult>.Ok(new FollowCheckResult { Started = true });
        }

        var follow = await _db.MediaFollows.AsNoTracking().FirstOrDefaultAsync(f => f.PersonId == personId.Value, ct);
        if (follow is null) return Resp<FollowCheckResult>.Fail("person not followed", RespCodes.NotFound);

        var added = await CheckFollowByIdAsync(follow.Id, ct);
        return Resp<FollowCheckResult>.Ok(new FollowCheckResult { CheckedPersons = 1, NewEntries = added });
    }

    /// <inheritdoc />
    public async Task<int> CheckAllAsync(CancellationToken ct = default)
    {
        // 名额在这里抢：宿主定时任务与手动「全部检查」都只会有一条真正在跑。
        if (!TryBeginCheckRun()) return 0;
        try
        {
            var ids = await _db.MediaFollows.AsNoTracking().OrderBy(f => f.Id).Select(f => f.Id).ToListAsync(ct);
            var added = 0;
            for (var i = 0; i < ids.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                added += await CheckFollowByIdAsync(ids[i], ct);
                // 最后一个之后不再等：关停时少等 250 ms。
                if (i < ids.Count - 1) await Task.Delay(_personDelay, ct);
            }
            return added;
        }
        finally
        {
            EndCheckRun();
        }
    }

    /// <summary>
    /// 抢占「整轮检查」的名额（宿主定时任务与手动「全部检查」共用）。
    /// 非阻塞：已有一轮在跑时直接返回 false，调用方据此告诉前端「已在运行」。
    /// </summary>
    public static bool TryBeginCheckRun() => RunGate.Wait(0);

    /// <summary>是否已有一轮整轮检查在跑（只用于给前端一个准确的 <c>started</c>）。</summary>
    public static bool IsRunInProgress => RunGate.CurrentCount == 0;

    /// <summary>归还「整轮检查」名额。只由拿到名额的一方在 finally 里调用。</summary>
    private static void EndCheckRun() => RunGate.Release();

    /// <summary>
    /// 检查一个人物（按关注行 id）：做差集、落库、更新 <c>LastCheckedAt</c> / <c>LastError</c>。
    /// 失败不抛 —— 调用方（宿主任务 / 手动检查）靠 <c>LastError</c> 了解原因。
    /// </summary>
    private async Task<int> CheckFollowByIdAsync(long followId, CancellationToken ct)
    {
        var follow = await _db.MediaFollows.FirstOrDefaultAsync(f => f.Id == followId, ct);
        if (follow is null) return 0;
        return await CheckFollowAsync(follow, ct);
    }

    /// <summary>差集实现（见类说明）。<paramref name="follow"/> 必须是被跟踪的实体。</summary>
    private async Task<int> CheckFollowAsync(MediaFollow follow, CancellationToken ct)
    {
        await PersonGate.WaitAsync(ct);
        try
        {
            string? error = null;
            var added = 0;
            var person = await _db.MediaPersons.AsNoTracking().FirstOrDefaultAsync(p => p.Id == follow.PersonId, ct);
            if (person is null || string.IsNullOrWhiteSpace(person.TmdbId))
            {
                error = "person has no tmdb id";
            }
            else
            {
                try
                {
                    var works = await _feedProvider.GetWorksAsync(person.TmdbId!, ct);
                    var existing = (await _db.MediaFollowEntries.AsNoTracking()
                            .Where(e => e.PersonId == follow.PersonId)
                            .Select(e => e.ExternalId)
                            .ToListAsync(ct))
                        .ToHashSet(StringComparer.Ordinal);

                    var now = _time.GetUtcNow();
                    // **倒序插入**：更新流按 Id 倒序翻页（SQLite 不支持 SQL 层按
                    // DateTimeOffset 排序），倒序写入才能让「同一批」在页面上回到数据源
                    // 给的顺序（代表作在前）。改成正序会让每位创作者的作品整体反排，
                    // 首次关注时最明显。
                    foreach (var w in works.Reverse())
                    {
                        // 差集键只有 ExternalId（与唯一索引一致）：TMDB 的 movie / tv 是两套 id，
                        // 同一个人名下可能撞号，此时只留先到的那部（cast 在前）。
                        if (w.ExternalId.Length == 0 || !existing.Add(w.ExternalId)) continue;
                        _db.MediaFollowEntries.Add(new MediaFollowEntry
                        {
                            FollowId = follow.Id,
                            PersonId = follow.PersonId,
                            MediaType = w.MediaType,
                            ExternalId = w.ExternalId,
                            Title = w.Title,
                            ReleaseDate = w.ReleaseDate,
                            PosterPath = w.PosterPath,
                            Role = w.Role,
                            FirstSeenAt = now,
                        });
                        added++;
                    }
                    follow.LastCheckedAt = now;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    error = Truncate(ex.Message);
                }
            }

            follow.LastError = error;
            await _db.SaveChangesAsync(ct);
            return added;
        }
        finally
        {
            PersonGate.Release();
        }
    }

    /// <summary>
    /// 手动「全部检查」的后台执行体。名额由 <see cref="CheckAllAsync"/> 自己抢与归还，
    /// 这里只负责换一个作用域（请求作用域随响应结束就释放了）并兜整轮异常。
    /// </summary>
    private async Task RunBackgroundCheckAsync()
    {
        try
        {
            if (_scopeFactory is null)
            {
                // 没有作用域工厂（单测直接 new）：就在本实例上跑。
                await CheckAllAsync(CancellationToken.None);
                return;
            }
            using var scope = _scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IMediaFollowService>().CheckAllAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // 单个人物的失败已在 CheckFollowAsync 里落到 LastError；这里只兜整轮级别的异常。
            _logger?.LogWarning(ex, "media follow background check failed");
        }
    }

    /// <summary>
    /// 当前用户能看到的人物 id 集合：已登录用户看全部；游客只看「名下至少有一条公开库条目」的人物。
    /// 口径与 <c>IMediaCatalogService.ListPersonsAsync</c> 一致 —— 那里没条目的人物本来就不列出。
    /// </summary>
    private async Task<HashSet<long>> ListVisiblePersonIdsAsync(User user, List<long> personIds, CancellationToken ct)
    {
        if (personIds.Count == 0) return [];
        if (user.Id != 0) return [.. personIds];

        var visible = new HashSet<long>();
        foreach (var chunk in Chunks(personIds.Distinct().ToList()))
        {
            var ids = chunk;
            var found = await (
                from r in _db.MediaItemPersons.AsNoTracking()
                join i in _db.MediaItems.AsNoTracking() on r.ItemId equals i.Id
                join l in _db.MediaLibraries.AsNoTracking() on i.LibraryId equals l.Id
                where !l.Private && ids.Contains(r.PersonId)
                select r.PersonId).ToListAsync(ct);
            foreach (var id in found) visible.Add(id);
        }
        return visible;
    }

    /// <summary>人物可见性：已登录恒可见；游客要求名下至少有一条公开库条目。</summary>
    private async Task<bool> CanSeePersonAsync(User user, long personId, CancellationToken ct)
        => (await ListVisiblePersonIdsAsync(user, [personId], ct)).Contains(personId);

    /// <summary>
    /// 按外部作品 id 取库里对口的条目：先按 <c>TmdbId</c> 对表，再按作品类型（movie/tv）匹配，
    /// 最后只留当前用户看得见的库（私有库条目对游客等于不存在）。
    /// </summary>
    private async Task<List<(string ExternalId, MediaLibraryType Type, long ItemId)>> LoadItemsByExternalAsync(
        List<string> externalIds, User user, CancellationToken ct)
    {
        var result = new List<(string, MediaLibraryType, long)>();
        if (externalIds.Count == 0) return result;

        List<(long Id, string TmdbId, MediaLibraryType Type, long LibraryId)> rows = [];
        foreach (var chunk in Chunks(externalIds))
        {
            var ids = chunk;
            rows.AddRange(await _db.MediaItems.AsNoTracking()
                .Where(i => i.TmdbId != null && ids.Contains(i.TmdbId))
                .Select(i => new ValueTuple<long, string, MediaLibraryType, long>(i.Id, i.TmdbId!, i.Type, i.LibraryId))
                .ToListAsync(ct));
        }
        if (rows.Count == 0) return result;

        var libraryIds = rows.Select(r => r.LibraryId).Distinct().ToList();
        var libraries = await _db.MediaLibraries.AsNoTracking()
            .Where(l => libraryIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, ct);

        // 已登录用户的 CanSeeLibrary 恒真，省掉查库；只有游客需要回表。
        result.AddRange(rows
            .Where(r => user.Id != 0
                        || (libraries.TryGetValue(r.LibraryId, out var lib) && _libraries.CanSeeLibrary(user, lib)))
            .Select(r => (r.TmdbId, r.Type, r.Id)));
        return result;
    }

    /// <summary>按外部作品 id 取「还没入库」的想看记录（已入库的想看由 ItemId 表达，不重复标）。</summary>
    private async Task<Dictionary<(string MediaType, string ExternalId), MediaWant>> LoadOpenWantsAsync(
        List<string> externalIds, CancellationToken ct)
    {
        var result = new Dictionary<(string, string), MediaWant>();
        if (externalIds.Count == 0) return result;
        foreach (var chunk in Chunks(externalIds))
        {
            var ids = chunk;
            var rows = await _db.MediaWants.AsNoTracking()
                .Where(w => w.ItemId == null && ids.Contains(w.ExternalId))
                .ToListAsync(ct);
            foreach (var w in rows) result.TryAdd((w.MediaType, w.ExternalId), w);
        }
        return result;
    }

    /// <summary>在一批对口条目里找某部作品；找不到或不只一条时取第一条。</summary>
    private static (string ExternalId, MediaLibraryType Type, long ItemId)? FindItem(
        List<(string ExternalId, MediaLibraryType Type, long ItemId)> items, string mediaType, string externalId)
    {
        var expected = string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase)
            ? MediaLibraryType.TvShow
            : MediaLibraryType.Movie;
        return items.FirstOrDefault(i => i.ExternalId == externalId && i.Type == expected) is var hit && hit.ItemId != 0
            ? hit
            : null;
    }

    /// <summary>把数据源给的海报路径变成绝对 URL（相对路径拼 TMDB 图片基地址）。</summary>
    private static string? ToAbsoluteUrl(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (MediaImages.IsRemote(path)) return path;
        return TmdbPersonFeedProvider.ImageBase + (path.StartsWith('/') ? string.Empty : "/") + path;
    }

    private static IEnumerable<List<T>> Chunks<T>(List<T> source)
    {
        for (var i = 0; i < source.Count; i += ChunkSize) yield return source.GetRange(i, Math.Min(ChunkSize, source.Count - i));
    }

    private static string Truncate(string message)
        => message.Length <= MaxErrorLength ? message : message[..MaxErrorLength];
}
