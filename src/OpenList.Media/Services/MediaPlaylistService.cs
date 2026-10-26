using Microsoft.EntityFrameworkCore;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

using OpenList.Media.Domain;
using OpenList.Media.Domain.Abstractions;

namespace OpenList.Media.Services;

/// <summary>
/// 播放列表服务实现。三条硬规则：
/// <list type="number">
/// <item>归属校验先行——不属于当前用户的列表一律按"不存在"返回，不给 403（否则等于确认了它的存在）。</item>
/// <item>加片时校验条目可见性——用户不能把自己看不到的条目塞进自己的列表。</item>
/// <item>展示时再过滤一遍——条目后来被移出用户可见范围（改了 BasePath、删了库）时，
/// 列表里只是看不到它，不会因为脏数据抛错。</item>
/// </list>
/// </summary>
public sealed class MediaPlaylistService : IMediaPlaylistService
{
    /// <summary>列表摘要里带的预览封面数量。</summary>
    private const int PreviewCount = 5;
    /// <summary>名称长度上限。</summary>
    private const int NameMaxLength = 100;
    /// <summary>描述长度上限。</summary>
    private const int DescriptionMaxLength = 500;
    /// <summary>重排时整体上移的偏移量，避开 UNIQUE(PlaylistId, Position)。</summary>
    private const int ReorderOffset = 1_000_000;

    private readonly OpenListDbContext _db;
    private readonly IMediaLibraryService _libraries;
    private readonly TimeProvider _time;

    /// <summary>创建播放列表服务实例。</summary>
    public MediaPlaylistService(OpenListDbContext db, IMediaLibraryService libraries, TimeProvider? time = null)
    {
        _db = db;
        _libraries = libraries;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<Resp<List<MediaPlaylistSummary>>> ListAsync(User user, CancellationToken ct = default)
    {
        // SQLite 不支持在 ORDER BY 里对 DateTimeOffset 生成表达式（EF 会抛
        // NotSupportedException），所以取回后按更新时间倒序在内存里排。
        // 单个用户的播放列表数量很小，客户端排序没有性能顾虑。
        var playlists = (await _db.MediaPlaylists.AsNoTracking()
                .Where(p => p.UserId == user.Id)
                .ToListAsync(ct))
            .OrderByDescending(p => p.UpdatedAt)
            .ThenByDescending(p => p.Id)
            .ToList();
        if (playlists.Count == 0) return Resp<List<MediaPlaylistSummary>>.Ok([]);

        var ids = playlists.Select(p => p.Id).ToList();
        var members = await _db.MediaPlaylistItems.AsNoTracking()
            .Where(i => ids.Contains(i.PlaylistId))
            .OrderBy(i => i.PlaylistId).ThenBy(i => i.Position)
            .Select(i => new { i.PlaylistId, i.ItemId })
            .ToListAsync(ct);
        var grouped = members
            .GroupBy(m => m.PlaylistId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ItemId).ToList());

        var summaries = playlists.Select(p =>
        {
            var itemIds = grouped.GetValueOrDefault(p.Id) ?? [];
            return new MediaPlaylistSummary
            {
                Id = p.Id,
                Name = p.Name,
                Description = p.Description,
                ItemCount = itemIds.Count,
                PreviewItemIds = itemIds.Take(PreviewCount).ToList(),
                CreatedAt = p.CreatedAt,
                UpdatedAt = p.UpdatedAt,
            };
        }).ToList();
        return Resp<List<MediaPlaylistSummary>>.Ok(summaries);
    }

    /// <inheritdoc />
    public async Task<Resp<MediaPlaylistDetail>> GetAsync(long id, User user, CancellationToken ct = default)
    {
        var playlist = await FindOwnedAsync(id, user.Id, ct);
        if (playlist is null) return Resp<MediaPlaylistDetail>.Fail("playlist not found", RespCodes.NotFound);
        return Resp<MediaPlaylistDetail>.Ok(await BuildDetailAsync(playlist, user, ct));
    }

    /// <inheritdoc />
    public async Task<Resp<MediaPlaylistSummary>> CreateAsync(CreateMediaPlaylistReq req, User user, CancellationToken ct = default)
    {
        var name = (req.Name ?? string.Empty).Trim();
        if (name.Length == 0) return Resp<MediaPlaylistSummary>.Fail("name is required", RespCodes.BadRequest);
        if (name.Length > NameMaxLength)
            return Resp<MediaPlaylistSummary>.Fail($"name must be at most {NameMaxLength} characters", RespCodes.BadRequest);
        var description = NormalizeDescription(req.Description);
        if (description?.Length > DescriptionMaxLength)
            return Resp<MediaPlaylistSummary>.Fail($"description must be at most {DescriptionMaxLength} characters", RespCodes.BadRequest);

        var now = _time.GetUtcNow();
        var playlist = new MediaPlaylist
        {
            UserId = user.Id,
            Name = name,
            Description = description,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.MediaPlaylists.Add(playlist);
        await _db.SaveChangesAsync(ct);
        return Resp<MediaPlaylistSummary>.Ok(ToSummary(playlist, []));
    }

    /// <inheritdoc />
    public async Task<Resp<MediaPlaylistSummary>> UpdateAsync(long id, UpdateMediaPlaylistReq req, User user, CancellationToken ct = default)
    {
        var playlist = await FindOwnedAsync(id, user.Id, ct);
        if (playlist is null) return Resp<MediaPlaylistSummary>.Fail("playlist not found", RespCodes.NotFound);

        if (req.Name is not null)
        {
            var name = req.Name.Trim();
            if (name.Length == 0) return Resp<MediaPlaylistSummary>.Fail("name cannot be empty", RespCodes.BadRequest);
            if (name.Length > NameMaxLength)
                return Resp<MediaPlaylistSummary>.Fail($"name must be at most {NameMaxLength} characters", RespCodes.BadRequest);
            playlist.Name = name;
        }
        if (req.Description is not null)
        {
            var description = NormalizeDescription(req.Description);
            if (description?.Length > DescriptionMaxLength)
                return Resp<MediaPlaylistSummary>.Fail($"description must be at most {DescriptionMaxLength} characters", RespCodes.BadRequest);
            playlist.Description = description;
        }

        playlist.UpdatedAt = _time.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        var itemIds = await _db.MediaPlaylistItems.AsNoTracking()
            .Where(i => i.PlaylistId == id).OrderBy(i => i.Position).Select(i => i.ItemId).ToListAsync(ct);
        return Resp<MediaPlaylistSummary>.Ok(ToSummary(playlist, itemIds));
    }

    /// <inheritdoc />
    public async Task<Resp> DeleteAsync(long id, User user, CancellationToken ct = default)
    {
        var playlist = await FindOwnedAsync(id, user.Id, ct);
        if (playlist is null) return Resp.Fail("playlist not found", RespCodes.NotFound);

        var members = await _db.MediaPlaylistItems.Where(i => i.PlaylistId == id).ToListAsync(ct);
        _db.MediaPlaylistItems.RemoveRange(members);
        _db.MediaPlaylists.Remove(playlist);
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    /// <inheritdoc />
    public async Task<Resp<MediaPlaylistDetail>> AddItemAsync(long id, long itemId, User user, CancellationToken ct = default)
    {
        var playlist = await FindOwnedAsync(id, user.Id, ct);
        if (playlist is null) return Resp<MediaPlaylistDetail>.Fail("playlist not found", RespCodes.NotFound);

        var item = await _db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return Resp<MediaPlaylistDetail>.Fail("item not found", RespCodes.NotFound);
        if (!_libraries.CanSeeItem(user, item)) return Resp<MediaPlaylistDetail>.Fail("permission denied", 403);

        var exists = await _db.MediaPlaylistItems.AnyAsync(i => i.PlaylistId == id && i.ItemId == itemId, ct);
        if (!exists)
        {
            var maxPosition = await _db.MediaPlaylistItems
                .Where(i => i.PlaylistId == id)
                .Select(i => (int?)i.Position)
                .MaxAsync(ct);
            _db.MediaPlaylistItems.Add(new MediaPlaylistItem
            {
                PlaylistId = id,
                ItemId = itemId,
                Position = (maxPosition ?? -1) + 1,
                AddedAt = _time.GetUtcNow(),
            });
            playlist.UpdatedAt = _time.GetUtcNow();
            await _db.SaveChangesAsync(ct);
        }
        return Resp<MediaPlaylistDetail>.Ok(await BuildDetailAsync(playlist, user, ct));
    }

    /// <inheritdoc />
    public async Task<Resp<MediaPlaylistDetail>> RemoveItemAsync(long id, long itemId, User user, CancellationToken ct = default)
    {
        var playlist = await FindOwnedAsync(id, user.Id, ct);
        if (playlist is null) return Resp<MediaPlaylistDetail>.Fail("playlist not found", RespCodes.NotFound);

        var member = await _db.MediaPlaylistItems
            .FirstOrDefaultAsync(i => i.PlaylistId == id && i.ItemId == itemId, ct);
        if (member is null) return Resp<MediaPlaylistDetail>.Ok(await BuildDetailAsync(playlist, user, ct));

        _db.MediaPlaylistItems.Remove(member);
        await _db.SaveChangesAsync(ct);

        // 压实位置，避免后续重排把空洞当成顺序。
        await CompactPositionsAsync(id, ct);
        playlist.UpdatedAt = _time.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        return Resp<MediaPlaylistDetail>.Ok(await BuildDetailAsync(playlist, user, ct));
    }

    /// <inheritdoc />
    public async Task<Resp<MediaPlaylistDetail>> ReorderAsync(long id, IReadOnlyList<long> itemIds, User user, CancellationToken ct = default)
    {
        var playlist = await FindOwnedAsync(id, user.Id, ct);
        if (playlist is null) return Resp<MediaPlaylistDetail>.Fail("playlist not found", RespCodes.NotFound);

        var members = await _db.MediaPlaylistItems.Where(i => i.PlaylistId == id).ToListAsync(ct);
        var current = members.OrderBy(m => m.Position).Select(m => m.ItemId).ToList();

        // 只认确实在列表里的 id；重复项只取第一次出现。
        var wanted = itemIds.Where(x => current.Contains(x)).Distinct().ToList();
        var ordered = wanted.Concat(current.Where(x => !wanted.Contains(x))).ToList();

        // 先整体挪到高位区间：直接原位赋值会撞上 UNIQUE(PlaylistId, Position)。
        var byItem = members.ToDictionary(m => m.ItemId);
        foreach (var member in members) member.Position += ReorderOffset;
        await _db.SaveChangesAsync(ct);
        for (var i = 0; i < ordered.Count; i++) byItem[ordered[i]].Position = i;
        playlist.UpdatedAt = _time.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        return Resp<MediaPlaylistDetail>.Ok(await BuildDetailAsync(playlist, user, ct));
    }

    /// <summary>按 id 取列表并校验归属；不属于该用户时返回 null。</summary>
    private async Task<MediaPlaylist?> FindOwnedAsync(long id, long userId, CancellationToken ct)
        => await _db.MediaPlaylists.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId, ct);

    /// <summary>把当前成员的位置重排成 0..n-1（保持既有相对顺序）。</summary>
    private async Task CompactPositionsAsync(long playlistId, CancellationToken ct)
    {
        var members = await _db.MediaPlaylistItems
            .Where(i => i.PlaylistId == playlistId)
            .OrderBy(i => i.Position)
            .ToListAsync(ct);
        if (members.Count == 0) return;

        var needsFix = members.Where((m, index) => m.Position != index).ToList();
        if (needsFix.Count == 0) return;

        foreach (var member in members) member.Position += ReorderOffset;
        await _db.SaveChangesAsync(ct);
        for (var i = 0; i < members.Count; i++) members[i].Position = i;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>组装详情：按位置取条目，并过滤掉当前用户已无权访问的条目。</summary>
    private async Task<MediaPlaylistDetail> BuildDetailAsync(MediaPlaylist playlist, User user, CancellationToken ct)
    {
        var itemIds = await _db.MediaPlaylistItems.AsNoTracking()
            .Where(i => i.PlaylistId == playlist.Id)
            .OrderBy(i => i.Position)
            .Select(i => i.ItemId)
            .ToListAsync(ct);

        var items = await _db.MediaItems.AsNoTracking()
            .Where(i => itemIds.Contains(i.Id))
            .ToListAsync(ct);
        var byId = items.ToDictionary(i => i.Id);

        return new MediaPlaylistDetail
        {
            Id = playlist.Id,
            Name = playlist.Name,
            Description = playlist.Description,
            Items = itemIds
                .Select(id => byId.GetValueOrDefault(id))
                .Where(i => i is not null && _libraries.CanSeeItem(user, i))
                .Select(i => i!)
                .ToList(),
            CreatedAt = playlist.CreatedAt,
            UpdatedAt = playlist.UpdatedAt,
        };
    }

    private MediaPlaylistSummary ToSummary(MediaPlaylist playlist, List<long> itemIds) => new()
    {
        Id = playlist.Id,
        Name = playlist.Name,
        Description = playlist.Description,
        ItemCount = itemIds.Count,
        PreviewItemIds = itemIds.Take(PreviewCount).ToList(),
        CreatedAt = playlist.CreatedAt,
        UpdatedAt = playlist.UpdatedAt,
    };

    /// <summary>描述归一化：去首尾空白，空串视为"无描述"。</summary>
    private static string? NormalizeDescription(string? raw)
    {
        if (raw is null) return null;
        var trimmed = raw.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
