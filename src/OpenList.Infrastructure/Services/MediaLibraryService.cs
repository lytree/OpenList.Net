using Microsoft.EntityFrameworkCore;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 媒体库管理服务。采用"全局库 + 路径过滤"模型：
/// 库由管理员（或具备 <see cref="User.CanManageMediaLibrary"/> 的用户）创建并全局共享，
/// 普通用户查询时按 <see cref="User.BasePath"/> 过滤可见范围。
/// </summary>
public sealed class MediaLibraryService : IMediaLibraryService
{
    private readonly OpenListDbContext _db;
    private readonly TimeProvider _time;

    /// <summary>创建媒体库管理服务实例。</summary>
    /// <param name="db">EF Core 数据库上下文。</param>
    /// <param name="time">时间源（可选，默认系统时钟）。</param>
    public MediaLibraryService(OpenListDbContext db, TimeProvider? time = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<Resp<MediaLibrary>> CreateAsync(CreateMediaLibraryReq req, User user, CancellationToken ct = default)
    {
        if (!user.CanManageMediaLibrary()) return Resp<MediaLibrary>.Fail("permission denied", 403);
        if (string.IsNullOrWhiteSpace(req.Name)) return Resp<MediaLibrary>.Fail("name is required", RespCodes.BadRequest);
        if (string.IsNullOrWhiteSpace(req.Path) || !req.Path.StartsWith('/'))
            return Resp<MediaLibrary>.Fail("path must be an absolute virtual path", RespCodes.BadRequest);

        var path = NormalizePath(req.Path);
        if (await _db.MediaLibraries.AnyAsync(l => l.Path == path, ct))
            return Resp<MediaLibrary>.Fail("library path already exists", RespCodes.Conflict);

        var now = _time.GetUtcNow();
        var lib = new MediaLibrary
        {
            Name = req.Name.Trim(),
            Path = path,
            Type = req.Type,
            AutoScan = req.AutoScan,
            MetadataLanguage = req.MetadataLanguage,
            CreatedBy = user.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.MediaLibraries.Add(lib);
        await _db.SaveChangesAsync(ct);
        return Resp<MediaLibrary>.Ok(lib);
    }

    /// <inheritdoc />
    public async Task<Resp> UpdateAsync(long id, UpdateMediaLibraryReq req, User user, CancellationToken ct = default)
    {
        if (!user.CanManageMediaLibrary()) return Resp.Fail("permission denied", 403);
        var lib = await _db.MediaLibraries.FindAsync([id], ct);
        if (lib is null) return Resp.Fail("library not found", RespCodes.NotFound);

        if (req.Name is not null) lib.Name = req.Name.Trim();
        if (req.Type is not null) lib.Type = req.Type.Value;
        if (req.AutoScan is not null) lib.AutoScan = req.AutoScan.Value;
        if (req.MetadataLanguage is not null) lib.MetadataLanguage = req.MetadataLanguage;
        lib.UpdatedAt = _time.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    /// <inheritdoc />
    public async Task<Resp> DeleteAsync(long id, User user, CancellationToken ct = default)
    {
        if (!user.CanManageMediaLibrary()) return Resp.Fail("permission denied", 403);
        var lib = await _db.MediaLibraries.FindAsync([id], ct);
        if (lib is null) return Resp.Fail("library not found", RespCodes.NotFound);

        // 级联清理：条目 → 文件/创作者关联/个人状态 → 扫描历史 → 库本身。
        var itemIds = await _db.MediaItems.Where(i => i.LibraryId == id).Select(i => i.Id).ToListAsync(ct);
        if (itemIds.Count > 0)
        {
            var states = await _db.UserMediaStates.Where(s => itemIds.Contains(s.ItemId)).ToListAsync(ct);
            var credits = await _db.MediaItemPersons.Where(r => itemIds.Contains(r.ItemId)).ToListAsync(ct);
            var files = await _db.MediaFiles.Where(f => itemIds.Contains(f.ItemId)).ToListAsync(ct);
            _db.UserMediaStates.RemoveRange(states);
            _db.MediaItemPersons.RemoveRange(credits);
            _db.MediaFiles.RemoveRange(files);
            _db.MediaItems.RemoveRange(await _db.MediaItems.Where(i => i.LibraryId == id).ToListAsync(ct));
        }
        _db.MediaScanLogs.RemoveRange(await _db.MediaScanLogs.Where(l => l.LibraryId == id).ToListAsync(ct));
        _db.MediaLibraries.Remove(lib);
        await _db.SaveChangesAsync(ct);
        return Resp.Ok();
    }

    /// <inheritdoc />
    public async Task<Resp<List<MediaLibrary>>> ListAsync(User user, CancellationToken ct = default)
    {
        var all = await _db.MediaLibraries.AsNoTracking().OrderBy(l => l.Id).ToListAsync(ct);
        var visible = all.Where(l => CanSeeLibrary(user, l)).ToList();
        return Resp<List<MediaLibrary>>.Ok(visible);
    }

    /// <inheritdoc />
    public async Task<Resp<MediaLibrary>> GetAsync(long id, User user, CancellationToken ct = default)
    {
        var lib = await _db.MediaLibraries.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);
        if (lib is null) return Resp<MediaLibrary>.Fail("library not found", RespCodes.NotFound);
        return CanSeeLibrary(user, lib)
            ? Resp<MediaLibrary>.Ok(lib)
            : Resp<MediaLibrary>.Fail("permission denied", 403);
    }

    /// <inheritdoc />
    public async Task<Resp<PageResp<MediaItem>>> ListItemsAsync(MediaItemQuery query, User user, CancellationToken ct = default)
    {
        var lib = await _db.MediaLibraries.AsNoTracking().FirstOrDefaultAsync(l => l.Id == query.LibraryId, ct);
        if (lib is null) return Resp<PageResp<MediaItem>>.Fail("library not found", RespCodes.NotFound);
        if (!CanSeeLibrary(user, lib)) return Resp<PageResp<MediaItem>>.Fail("permission denied", 403);

        var q = _db.MediaItems.AsNoTracking().Where(i => i.LibraryId == query.LibraryId);
        if (query.Status is not null) q = q.Where(i => i.Status == query.Status);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var kw = query.Keyword.Trim();
            q = q.Where(i => i.Title.Contains(kw));
        }
        // 条目级 BasePath 过滤（全局库可能被多个用户共享，仅返回其目录范围内的条目）。
        var items = await q.OrderBy(i => i.Year).ThenBy(i => i.Title).ToListAsync(ct);
        var visible = items.Where(i => IsPathVisible(user, i.DirectoryPath)).ToList();

        var page = Math.Max(1, query.Page);
        var perPage = Math.Clamp(query.PerPage, 1, 200);
        var slice = visible.Skip((page - 1) * perPage).Take(perPage).ToList();
        return Resp<PageResp<MediaItem>>.Ok(new PageResp<MediaItem> { Content = slice, Total = visible.Count });
    }

    /// <inheritdoc />
    public async Task<Resp<MediaItem>> GetItemAsync(long itemId, User user, CancellationToken ct = default)
    {
        var item = await _db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return Resp<MediaItem>.Fail("item not found", RespCodes.NotFound);
        if (!IsPathVisible(user, item.DirectoryPath)) return Resp<MediaItem>.Fail("permission denied", 403);
        return Resp<MediaItem>.Ok(item);
    }

    /// <inheritdoc />
    public async Task<Resp<List<MediaFile>>> ListItemFilesAsync(long itemId, User user, CancellationToken ct = default)
    {
        var item = await _db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return Resp<List<MediaFile>>.Fail("item not found", RespCodes.NotFound);
        if (!IsPathVisible(user, item.DirectoryPath)) return Resp<List<MediaFile>>.Fail("permission denied", 403);

        var files = await _db.MediaFiles.AsNoTracking()
            .Where(f => f.ItemId == itemId)
            .OrderBy(f => f.Path)
            .ToListAsync(ct);
        return Resp<List<MediaFile>>.Ok(files);
    }

    /// <inheritdoc />
    public bool CanSeeLibrary(User user, MediaLibrary library)
    {
        if (user.IsAdmin()) return true;
        if (string.IsNullOrEmpty(user.BasePath) || user.BasePath == "/") return true;
        var basePath = NormalizePath(user.BasePath);
        var libPath = NormalizePath(library.Path);
        // 库在用户根目录之下，或用户根目录在库之下（共享库场景），均视为可见；
        // 具体条目再由 IsPathVisible 过滤。
        return IsUnder(libPath, basePath) || IsUnder(basePath, libPath);
    }

    /// <summary>判断条目路径是否在用户根目录范围内（管理员与根用户恒可见）。</summary>
    public bool CanSeeItem(User user, MediaItem item) => IsPathVisible(user, item.DirectoryPath);

    /// <summary>判断条目路径是否在用户根目录范围内（管理员与根用户恒可见）。</summary>
    private static bool IsPathVisible(User user, string path)
    {
        if (user.IsAdmin()) return true;
        if (string.IsNullOrEmpty(user.BasePath) || user.BasePath == "/") return true;
        return IsUnder(NormalizePath(path), NormalizePath(user.BasePath));
    }

    /// <summary>判断 <paramref name="path"/> 是否等于 <paramref name="basePath"/> 或位于其下。</summary>
    internal static bool IsUnder(string path, string basePath)
    {
        if (basePath == "/") return true;
        if (path == basePath) return true;
        return path.StartsWith(basePath + "/", StringComparison.Ordinal);
    }

    /// <summary>标准化虚拟路径：补前导斜杠、去尾斜杠、折叠重复斜杠。</summary>
    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        var p = path.Trim().Replace('\\', '/');
        while (p.Contains("//", StringComparison.Ordinal)) p = p.Replace("//", "/", StringComparison.Ordinal);
        if (!p.StartsWith('/')) p = "/" + p;
        if (p.Length > 1) p = p.TrimEnd('/');
        return p.Length == 0 ? "/" : p;
    }
}
