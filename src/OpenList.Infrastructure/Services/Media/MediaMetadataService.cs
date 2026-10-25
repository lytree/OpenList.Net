using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services.Media;

/// <summary>
/// 媒体元数据服务：刮削调度 + NFO/海报写回。
/// 刮削结果写入数据库（全局共享），同时把 Kodi 兼容的 <c>.nfo</c>、<c>poster.jpg</c>、
/// <c>fanart.jpg</c> 写回媒体目录，使 Jellyfin 等外部媒体中心可直接识别。
/// </summary>
public sealed partial class MediaMetadataService : IMediaMetadataService
{
    /// <summary>写文件时使用的内部管理员身份。</summary>
    private static readonly User WriterUser = new() { Id = 0, Username = "media-scraper", Role = UserRole.Admin };

    private readonly OpenListDbContext _db;
    private readonly IMediaLibraryService _libraries;
    private readonly IFileSystemService _fs;
    private readonly IMetadataProvider _provider;
    private readonly ISettingService _settings;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;

    /// <summary>创建元数据服务实例。</summary>
    public MediaMetadataService(
        OpenListDbContext db,
        IMediaLibraryService libraries,
        IFileSystemService fs,
        IMetadataProvider provider,
        ISettingService settings,
        IServiceScopeFactory scopeFactory,
        TimeProvider? time = null)
    {
        _db = db;
        _libraries = libraries;
        _fs = fs;
        _provider = provider;
        _settings = settings;
        _scopeFactory = scopeFactory;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<Resp<MediaItem>> ScrapeItemAsync(long itemId, User user, CancellationToken ct = default)
    {
        var item = await _db.MediaItems.FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return Resp<MediaItem>.Fail("item not found", RespCodes.NotFound);
        if (!_libraries.CanSeeItem(user, item)) return Resp<MediaItem>.Fail("permission denied", 403);

        var library = await _db.MediaLibraries.AsNoTracking().FirstOrDefaultAsync(l => l.Id == item.LibraryId, ct);
        if (library is null) return Resp<MediaItem>.Fail("library not found", RespCodes.NotFound);

        var language = await ResolveLanguageAsync(library, ct);
        var results = await _provider.SearchAsync(item.Title, item.Year, item.Type, language, ct);
        var best = PickBest(item, results);
        if (best is null)
        {
            item.Status = MediaItemStatus.Unmatched;
            item.UpdatedAt = _time.GetUtcNow();
            await _db.SaveChangesAsync(ct);
            return Resp<MediaItem>.Fail("no metadata match found", RespCodes.NotFound);
        }

        var meta = await _provider.GetAsync(best.Id, item.Type, language, ct);
        if (meta is null) return Resp<MediaItem>.Fail("metadata detail unavailable", 502);

        await ApplyAsync(item, meta, language, ct);
        return Resp<MediaItem>.Ok(item);
    }

    /// <inheritdoc />
    public async Task<Resp<MediaItem>> MatchItemAsync(long itemId, string providerId, User user, CancellationToken ct = default)
    {
        var item = await _db.MediaItems.FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return Resp<MediaItem>.Fail("item not found", RespCodes.NotFound);
        if (!_libraries.CanSeeItem(user, item)) return Resp<MediaItem>.Fail("permission denied", 403);
        if (string.IsNullOrWhiteSpace(providerId)) return Resp<MediaItem>.Fail("provider id is required", RespCodes.BadRequest);

        var meta = await _provider.GetAsync(providerId, item.Type, await ResolveLanguageAsync(null, ct), ct);
        if (meta is null) return Resp<MediaItem>.Fail("metadata detail unavailable", 502);

        await ApplyAsync(item, meta, await ResolveLanguageAsync(null, ct), ct);
        return Resp<MediaItem>.Ok(item);
    }

    /// <inheritdoc />
    public async Task<Resp> ScrapeLibraryAsync(long libraryId, User user, CancellationToken ct = default)
    {
        if (!user.CanManageMediaLibrary()) return Resp.Fail("permission denied", 403);
        var library = await _db.MediaLibraries.AsNoTracking().FirstOrDefaultAsync(l => l.Id == libraryId, ct);
        if (library is null) return Resp.Fail("library not found", RespCodes.NotFound);
        if (!await _provider.IsConfiguredAsync(ct))
            return Resp.Fail("metadata provider is not configured (set media_tmdb_api_key)", RespCodes.BadRequest);

        // 后台批量刮削：每条之间留出间隔，避免触发 provider 频率限制。
        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<OpenListDbContext>();
            var fs = sp.GetRequiredService<IFileSystemService>();
            var provider = sp.GetRequiredService<IMetadataProvider>();
            var settings = sp.GetRequiredService<ISettingService>();

            var pending = await db.MediaItems
                .Where(i => i.LibraryId == libraryId && i.Status == MediaItemStatus.Pending)
                .OrderBy(i => i.Id)
                .Take(2000)
                .ToListAsync(CancellationToken.None);

            var language = library.MetadataLanguage;
            if (string.IsNullOrWhiteSpace(language))
            {
                var langResp = await settings.GetAsync("media_metadata_language", CancellationToken.None);
                language = langResp.Code == 200 && !string.IsNullOrWhiteSpace(langResp.Data?.Value) ? langResp.Data!.Value : "zh-CN";
            }

            foreach (var item in pending)
            {
                try
                {
                    var results = await provider.SearchAsync(item.Title, item.Year, item.Type, language, CancellationToken.None);
                    var best = PickBest(item, results);
                    if (best is null)
                    {
                        item.Status = MediaItemStatus.Unmatched;
                        await db.SaveChangesAsync(CancellationToken.None);
                        continue;
                    }
                    var meta = await provider.GetAsync(best.Id, item.Type, language, CancellationToken.None);
                    if (meta is null) continue;

                    await ApplyCoreAsync(db, fs, provider, _time, item, meta, CancellationToken.None);
                }
                catch (Exception)
                {
                    // 单条失败不影响整批；条目保持 Pending 以便下次重试。
                }
                await Task.Delay(250, CancellationToken.None);
            }
        }, CancellationToken.None);

        return Resp.Ok();
    }

    /// <inheritdoc />
    public async Task<Resp<List<MediaItemPersonCredit>>> ListItemCreditsAsync(long itemId, User user, CancellationToken ct = default)
    {
        var item = await _db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return Resp<List<MediaItemPersonCredit>>.Fail("item not found", RespCodes.NotFound);
        if (!_libraries.CanSeeItem(user, item)) return Resp<List<MediaItemPersonCredit>>.Fail("permission denied", 403);

        var rows = await (from r in _db.MediaItemPersons.AsNoTracking()
                          join p in _db.MediaPersons.AsNoTracking() on r.PersonId equals p.Id
                          where r.ItemId == itemId
                          orderby r.Order, r.Id
                          select new MediaItemPersonCredit
                          {
                              PersonId = p.Id,
                              Name = p.Name,
                              ProfilePath = p.ProfilePath,
                              Role = r.Role,
                              Character = r.Character,
                              Order = r.Order,
                          }).ToListAsync(ct);
        return Resp<List<MediaItemPersonCredit>>.Ok(rows);
    }

    /// <inheritdoc />
    public async Task<Resp<PersonAggregate>> GetPersonAsync(long personId, User user, CancellationToken ct = default)
    {
        var person = await _db.MediaPersons.AsNoTracking().FirstOrDefaultAsync(p => p.Id == personId, ct);
        if (person is null) return Resp<PersonAggregate>.Fail("person not found", RespCodes.NotFound);

        var rows = await (from r in _db.MediaItemPersons.AsNoTracking()
                          join i in _db.MediaItems.AsNoTracking() on r.ItemId equals i.Id
                          where r.PersonId == personId
                          orderby i.Year descending, i.Title
                          select new { Item = i, r.Role, r.Character }).ToListAsync(ct);

        var visible = rows.Where(x => _libraries.CanSeeItem(user, x.Item))
            .Select(x => new PersonAggregateItem { Item = x.Item, Role = x.Role, Character = x.Character })
            .ToList();
        return Resp<PersonAggregate>.Ok(new PersonAggregate { Person = person, Items = visible });
    }

    /// <summary>把元数据写入条目、创作者关联与磁盘（NFO/图片）。</summary>
    private async Task ApplyAsync(MediaItem item, MediaMetadata meta, string language, CancellationToken ct)
        => await ApplyCoreAsync(_db, _fs, _provider, _time, item, meta, ct);

    /// <summary>元数据落库 + 落盘的核心实现（静态以便批量任务复用独立作用域）。</summary>
    private static async Task ApplyCoreAsync(
        OpenListDbContext db, IFileSystemService fs, IMetadataProvider provider, TimeProvider time,
        MediaItem item, MediaMetadata meta, CancellationToken ct)
    {
        item.Title = string.IsNullOrWhiteSpace(meta.Title) ? item.Title : meta.Title;
        item.OriginalTitle = meta.OriginalTitle;
        item.Year = meta.Year ?? item.Year;
        item.TmdbId = meta.Id;
        item.Overview = meta.Overview;
        item.Rating = meta.Rating;
        item.GenresJson = meta.Genres.Count > 0 ? JsonSerializer.Serialize(meta.Genres) : item.GenresJson;
        item.Status = MediaItemStatus.Matched;
        item.UpdatedAt = time.GetUtcNow();

        var dir = NfoDirectory(item);

        // 图片：先下载到内存，写回成功后才记录路径。
        if (!string.IsNullOrWhiteSpace(meta.PosterPath))
        {
            var bytes = await provider.GetImageAsync(meta.PosterPath, ct);
            if (bytes is not null && await WriteFileAsync(fs, dir + "/poster.jpg", bytes, ct))
                item.PosterPath = dir + "/poster.jpg";
        }
        if (!string.IsNullOrWhiteSpace(meta.BackdropPath))
        {
            var bytes = await provider.GetImageAsync(meta.BackdropPath, ct);
            if (bytes is not null && await WriteFileAsync(fs, dir + "/fanart.jpg", bytes, ct))
                item.BackdropPath = dir + "/fanart.jpg";
        }

        await UpsertPersonsAsync(db, time, item, meta, ct);
        await WriteNfoAsync(fs, item, meta, dir, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>写入 Kodi 兼容 NFO（电影用 movie，剧集用 tvshow）。</summary>
    private static async Task WriteNfoAsync(IFileSystemService fs, MediaItem item, MediaMetadata meta, string dir, CancellationToken ct)
    {
        var root = new XElement(item.Type == MediaLibraryType.TvShow ? "tvshow" : "movie");
        root.Add(new XElement("title", item.Title));
        if (!string.IsNullOrWhiteSpace(item.OriginalTitle)) root.Add(new XElement("originaltitle", item.OriginalTitle));
        if (item.Year is not null) root.Add(new XElement("year", item.Year));
        if (!string.IsNullOrWhiteSpace(item.Overview)) root.Add(new XElement("plot", item.Overview));
        if (item.Rating is not null) root.Add(new XElement("rating", item.Rating.Value.ToString("0.0")));
        foreach (var g in meta.Genres) root.Add(new XElement("genre", g));
        root.Add(new XElement("uniqueid", new XAttribute("type", "tmdb"), new XAttribute("default", "true"), meta.Id));

        foreach (var d in meta.Persons.Where(p => p.Role == "director")) root.Add(new XElement("director", d.Name));
        foreach (var c in meta.Persons.Where(p => p.Role == "creator")) root.Add(new XElement("showtitle", c.Name));
        // Kodi 的 actor 为多个独立节点，逐个追加。
        foreach (var a in meta.Persons.Where(p => p.Role == "actor"))
        {
            var actor = new XElement("actor", new XElement("name", a.Name), new XElement("order", a.Order));
            if (!string.IsNullOrWhiteSpace(a.Character)) actor.Add(new XElement("role", a.Character));
            root.Add(actor);
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root);
        var bytes = Encoding.UTF8.GetBytes(doc.Declaration + Environment.NewLine + doc);
        await WriteFileAsync(fs, dir + "/" + NfoFileName(item) + ".nfo", bytes, ct);
    }

    /// <summary>写文件到 VFS；失败返回 false（元数据仍保留在数据库）。</summary>
    private static async Task<bool> WriteFileAsync(IFileSystemService fs, string path, byte[] bytes, CancellationToken ct)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var resp = await fs.PutStreamAsync(path, ms, WriterUser, ct);
            return resp.Code == 200;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>条目对应的目录（若条目键是库根散文件，则取其所在目录）。</summary>
    private static string NfoDirectory(MediaItem item)
    {
        var dir = item.DirectoryPath;
        var ext = Path.GetExtension(dir);
        if (!string.IsNullOrEmpty(ext) && VideoExtensions.Contains(ext))
            return dir[..dir.LastIndexOf('/')];
        return dir;
    }

    /// <summary>NFO 文件名：使用条目目录名（Jellyfin/Kodi 均识别 &lt;目录名&gt;.nfo）；散文件用文件名。</summary>
    private static string NfoFileName(MediaItem item)
    {
        var dir = item.DirectoryPath;
        var ext = Path.GetExtension(dir);
        var name = !string.IsNullOrEmpty(ext) && VideoExtensions.Contains(ext)
            ? Path.GetFileNameWithoutExtension(dir)
            : dir[(dir.LastIndexOf('/') + 1)..];
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrEmpty(clean) ? "movie" : clean;
    }

    /// <summary>按 (Name, TmdbId) 去重写入创作者，并重建条目的创作者关联。</summary>
    private static async Task UpsertPersonsAsync(OpenListDbContext db, TimeProvider time, MediaItem item, MediaMetadata meta, CancellationToken ct)
    {
        var existingLinks = await db.MediaItemPersons.Where(r => r.ItemId == item.Id).ToListAsync(ct);
        db.MediaItemPersons.RemoveRange(existingLinks);
        await db.SaveChangesAsync(ct);

        foreach (var p in meta.Persons.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
        {
            var person = await db.MediaPersons.FirstOrDefaultAsync(x => x.Name == p.Name && x.TmdbId == p.ProviderId, ct);
            if (person is null)
            {
                person = new MediaPerson
                {
                    Name = p.Name,
                    TmdbId = p.ProviderId,
                    ProfilePath = p.ProfilePath,
                    CreatedAt = time.GetUtcNow(),
                    UpdatedAt = time.GetUtcNow(),
                };
                db.MediaPersons.Add(person);
                await db.SaveChangesAsync(ct);
            }
            db.MediaItemPersons.Add(new MediaItemPerson
            {
                ItemId = item.Id,
                PersonId = person.Id,
                Role = p.Role,
                Character = p.Character,
                Order = p.Order,
            });
        }
    }

    /// <summary>扫描器与服务共用的视频扩展名集合（判定条目键是否为散文件）。</summary>
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts",
        ".m2ts", ".mpg", ".mpeg", ".rmvb", ".rm", ".3gp", ".vob", ".iso", ".strm",
    };

    /// <summary>选择最佳匹配：标题归一化后完全相等优先，其次年份匹配，最后取首个结果。</summary>
    private static MetadataSearchResult? PickBest(MediaItem item, List<MetadataSearchResult> results)
    {
        if (results.Count == 0) return null;
        var target = Normalize(item.Title);
        var exact = results.Where(r => Normalize(r.Title) == target).ToList();
        if (exact.Count > 0)
        {
            if (item.Year is not null)
            {
                var yearMatch = exact.FirstOrDefault(r => r.Year is not null && Math.Abs(r.Year.Value - item.Year.Value) <= 1);
                if (yearMatch is not null) return yearMatch;
            }
            return exact[0];
        }
        if (item.Year is not null)
        {
            var yearMatch = results.FirstOrDefault(r => r.Year is not null && Math.Abs(r.Year.Value - item.Year.Value) <= 1);
            if (yearMatch is not null) return yearMatch;
        }
        return results[0];
    }

    /// <summary>标题归一化：去标点、空白、大小写，便于宽松比较。</summary>
    private static string Normalize(string s)
        => NonWordRegex().Replace(s.ToLowerInvariant(), string.Empty);

    /// <summary>解析刮削语言：库级优先，其次全局设置，默认 zh-CN。</summary>
    private async Task<string> ResolveLanguageAsync(MediaLibrary? library, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(library?.MetadataLanguage)) return library.MetadataLanguage!;
        var resp = await _settings.GetAsync("media_metadata_language", ct);
        var value = resp.Code == 200 ? resp.Data?.Value : null;
        return string.IsNullOrWhiteSpace(value) ? "zh-CN" : value;
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]")]
    private static partial Regex NonWordRegex();
}
