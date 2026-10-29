using Microsoft.EntityFrameworkCore;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

using OpenList.Media.Domain;
using OpenList.Media.Domain.Abstractions;

namespace OpenList.Media.Services;

/// <summary>
/// 观看会话：把播放心跳折成"一次连续观看一行"，并提供历史与逐日时长统计。
///
/// <para>
/// 三个常量决定了这张表的口径，改动前先想清楚副作用：
/// </para>
/// <list type="bullet">
/// <item><see cref="MaxStepSeconds"/>：单次上报的向前步进上限。播放器每 10 秒上报一次，
/// 正常步进就是 ±10 秒；超过 5 分钟只可能是拖了进度条 —— 那种"跳过去"不该被算成看过。</item>
/// <item><see cref="IdleTimeout"/>：会话开着但两小时没心跳，视为已结束（崩溃/断电兜底）。
/// 判定只发生在读取侧，不写回库：这个字段只在"这段历史怎么算"时有意义。</item>
/// <item><see cref="MaxFillDays"/>：逐日曲线的点数上限。三年的日线没人看，图也画不下。</item>
/// </list>
/// </summary>
public sealed class WatchSessionService : IWatchSessionService
{
    /// <summary>单次上报允许计入观看时长的最大向前步进（秒）。</summary>
    public const int MaxStepSeconds = 300;

    /// <summary>无心跳超过该时长，读取侧把会话视为已结束。</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(2);

    /// <summary>逐日曲线最多点数（天）。</summary>
    public const int MaxFillDays = 366;

    private readonly OpenListDbContext _db;
    private readonly IMediaLibraryService _libraries;
    private readonly TimeProvider _time;

    /// <summary>创建观看会话服务。</summary>
    public WatchSessionService(OpenListDbContext db, IMediaLibraryService libraries, TimeProvider? time = null)
    {
        _db = db;
        _libraries = libraries;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<Resp<MediaWatchSession>> ReportAsync(ReportWatchReq req, User user, CancellationToken ct = default)
    {
        var token = (req.Token ?? string.Empty).Trim();
        if (token.Length == 0)
            return Resp<MediaWatchSession>.Fail("token is required", RespCodes.BadRequest);
        if (req.PositionSeconds < 0)
            return Resp<MediaWatchSession>.Fail("position_seconds must be non-negative", RespCodes.BadRequest);

        var item = await _db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == req.ItemId, ct);
        if (item is null) return Resp<MediaWatchSession>.Fail("item not found", RespCodes.NotFound);
        if (!_libraries.CanSeeItem(user, item)) return Resp<MediaWatchSession>.Fail("permission denied", 403);

        var now = _time.GetUtcNow();
        var session = await _db.MediaWatchSessions
            .FirstOrDefaultAsync(s => s.UserId == user.Id && s.Token == token, ct);

        if (session is null)
        {
            session = new MediaWatchSession
            {
                UserId = user.Id,
                ItemId = req.ItemId,
                FileId = req.FileId,
                Token = token,
                StartedAt = now,
                LastAt = now,
                StartPositionSeconds = req.PositionSeconds,
                LastPositionSeconds = req.PositionSeconds,
                MaxPositionSeconds = req.PositionSeconds,
            };
            _db.MediaWatchSessions.Add(session);
        }
        else if (session.EndedAt is not null)
        {
            // 已结束的会话不因"迟到的上报"复活：历史一旦写下就别再改，
            // 否则关播放器后那个 2 秒兜底补写会把 EndedAt 又抹掉。
            return Resp<MediaWatchSession>.Ok(session);
        }
        else
        {
            // 同一个 token 换了条目 = 客户端复用 bug。把两条历史糅在一起比报错更难查。
            if (session.ItemId != req.ItemId)
                return Resp<MediaWatchSession>.Fail("token already used for another item", RespCodes.BadRequest);

            var delta = req.PositionSeconds - session.LastPositionSeconds;
            if (delta > 0 && delta <= MaxStepSeconds) session.WatchedSeconds += delta;
            session.LastPositionSeconds = req.PositionSeconds;
            if (req.PositionSeconds > session.MaxPositionSeconds) session.MaxPositionSeconds = req.PositionSeconds;
            session.LastAt = now;
            if (req.FileId is not null) session.FileId = req.FileId;
        }

        if (req.Closing && session.EndedAt is null)
        {
            session.EndedAt = now;
            session.Completed = req.Completed;
        }

        await _db.SaveChangesAsync(ct);
        return Resp<MediaWatchSession>.Ok(session);
    }

    /// <inheritdoc />
    public async Task<Resp<List<WatchHistoryEntry>>> ListHistoryAsync(
        int days, int limit, User user, CancellationToken ct = default)
    {
        var cutoff = TasteWindow.Cutoff(days, _time.GetUtcNow());
        // SQLite 不支持 DateTimeOffset 的比较（EF Core 明确列出的限制），
        // 所以窗口过滤在内存里做 —— 行数上限是"这个用户自己的会话数"，与库大小无关。
        var sessions = await _db.MediaWatchSessions.AsNoTracking()
            .Where(s => s.UserId == user.Id)
            .ToListAsync(ct);
        if (cutoff is not null) sessions = sessions.Where(s => s.StartedAt >= cutoff.Value).ToList();
        if (sessions.Count == 0) return Resp<List<WatchHistoryEntry>>.Ok([]);

        var itemIds = sessions.Select(s => s.ItemId).Distinct().ToList();
        var items = await _db.MediaItems.AsNoTracking()
            .Where(i => itemIds.Contains(i.Id))
            .ToListAsync(ct);
        var visible = items.Where(i => _libraries.CanSeeItem(user, i)).ToDictionary(i => i.Id);

        var size = Math.Clamp(limit, 1, 200);
        var entries = sessions
            .Where(s => visible.ContainsKey(s.ItemId))
            .GroupBy(s => s.ItemId)
            .Select(g => new WatchHistoryEntry
            {
                ItemId = g.Key,
                Title = visible[g.Key].Title,
                PosterPath = visible[g.Key].PosterPath,
                Year = visible[g.Key].Year,
                // 开着的会话用最后心跳时间：崩溃留下的残局不该显示成"刚刚看过"
                LastAt = g.Max(s => s.EndedAt ?? s.LastAt),
                WatchedSeconds = g.Sum(s => s.WatchedSeconds),
                Completed = g.Any(s => s.Completed),
            })
            .OrderByDescending(e => e.LastAt)
            .Take(size)
            .ToList();

        return Resp<List<WatchHistoryEntry>>.Ok(entries);
    }

    /// <inheritdoc />
    public async Task<Resp<WatchStats>> GetStatsAsync(int days, User user, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var windowDays = TasteWindow.Normalize(days);
        var cutoff = TasteWindow.Cutoff(days, now);

        var query = _db.MediaWatchSessions.AsNoTracking().Where(s => s.UserId == user.Id);
        var sessions = await query.ToListAsync(ct);
        // 同上：DateTimeOffset 的比较只能发生在内存里
        if (cutoff is not null) sessions = sessions.Where(s => s.StartedAt >= cutoff.Value).ToList();

        // 统计同样按可见性过滤：别人库里的会话不该出现在我的时长曲线里
        var itemIds = sessions.Select(s => s.ItemId).Distinct().ToList();
        var visible = itemIds.Count == 0
            ? []
            : (await _db.MediaItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToListAsync(ct))
                .Where(i => _libraries.CanSeeItem(user, i))
                .Select(i => i.Id)
                .ToHashSet();

        var perDay = sessions
            .Where(s => visible.Contains(s.ItemId))
            .GroupBy(s => s.StartedAt.UtcDateTime.ToString("yyyy-MM-dd"))
            .ToDictionary(g => g.Key, g => g.Sum(s => s.WatchedSeconds));

        // 补零：直接画图时不需要前端再补一次日期轴
        var fillDays = windowDays == 0 ? MaxFillDays : Math.Min(windowDays, MaxFillDays);
        var start = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-(fillDays - 1));
        var daysOut = new List<WatchDailyStat>(fillDays);
        for (var i = 0; i < fillDays; i++)
        {
            var date = start.AddDays(i).ToString("yyyy-MM-dd");
            daysOut.Add(new WatchDailyStat { Date = date, Seconds = perDay.GetValueOrDefault(date) });
        }

        return Resp<WatchStats>.Ok(new WatchStats
        {
            WindowDays = windowDays,
            // 总量按会话求和，不受曲线补零范围的限制
            TotalSeconds = perDay.Values.Sum(),
            Days = daysOut,
        });
    }
}
