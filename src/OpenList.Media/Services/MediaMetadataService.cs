using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

using OpenList.Media.Domain;
using OpenList.Media.Domain.Abstractions;
using OpenList.Media.Providers;

namespace OpenList.Media.Services;

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
    private readonly IMetadataProvider? _codeProvider;
    private readonly ISettingService _settings;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MediaScrapeCoordinator _scrape;
    private readonly TimeProvider _time;

    /// <summary>创建元数据服务实例。</summary>
    /// <param name="db">数据库上下文。</param>
    /// <param name="libraries">媒体库服务（可见性判定）。</param>
    /// <param name="fs">文件系统服务（NFO/图片写回）。</param>
    /// <param name="providers">全部已注册 provider（默认取 Name=tmdb 的那个，code 按名解析）。</param>
    /// <param name="settings">设置服务。</param>
    /// <param name="scopeFactory">作用域工厂（批量刮削后台任务）。</param>
    /// <param name="scrape">整库刮削运行态（单例；本服务是 Scoped，持有不了跨请求状态）。</param>
    /// <param name="time">时间源。</param>
    public MediaMetadataService(
        OpenListDbContext db,
        IMediaLibraryService libraries,
        IFileSystemService fs,
        IEnumerable<IMetadataProvider> providers,
        ISettingService settings,
        IServiceScopeFactory scopeFactory,
        MediaScrapeCoordinator scrape,
        TimeProvider? time = null)
    {
        _db = db;
        _libraries = libraries;
        _fs = fs;
        var all = providers.ToArray();
        _provider = all.FirstOrDefault(p => string.Equals(p.Name, "tmdb", StringComparison.OrdinalIgnoreCase)) ?? all[0];
        _codeProvider = all.FirstOrDefault(p => string.Equals(p.Name, "code", StringComparison.OrdinalIgnoreCase));
        _settings = settings;
        _scopeFactory = scopeFactory;
        _scrape = scrape;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<Resp<MediaItem>> ScrapeItemAsync(long itemId, User user, string? providerName = null, CancellationToken ct = default)
    {
        var item = await _db.MediaItems.FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return Resp<MediaItem>.Fail("item not found", RespCodes.NotFound);
        if (!_libraries.CanSeeItem(user, item)) return Resp<MediaItem>.Fail("permission denied", 403);

        var library = await _db.MediaLibraries.AsNoTracking().FirstOrDefaultAsync(l => l.Id == item.LibraryId, ct);
        if (library is null) return Resp<MediaItem>.Fail("library not found", RespCodes.NotFound);

        var provider = await PickProviderAsync(item, providerName, ct);
        if (provider is null)
            return Resp<MediaItem>.Fail($"unknown metadata provider '{providerName}'", RespCodes.BadRequest);
        if (!await provider.IsConfiguredAsync(ct))
        {
            var hint = string.Equals(provider.Name, "code", StringComparison.OrdinalIgnoreCase)
                ? $"metadata provider 'code' is not configured (set {CodeMetadataProvider.ApiUrlSetting})"
                : "metadata provider is not configured (set media_tmdb_api_key)";
            return Resp<MediaItem>.Fail(hint, RespCodes.BadRequest);
        }

        var language = await ResolveLanguageAsync(library, ct);

        // code provider 没有"搜索候选"：番号即 id —— 条目已有番号直接用，
        // 否则从条目路径/标题里现场解析。
        string? providerId;
        if (string.Equals(provider.Name, "code", StringComparison.OrdinalIgnoreCase))
        {
            providerId = item.Code ?? MediaNaming.ExtractCode(item.DirectoryPath) ?? MediaNaming.ExtractCode(item.Title);
            if (providerId is null)
            {
                item.Status = MediaItemStatus.Unmatched;
                item.UpdatedAt = _time.GetUtcNow();
                await _db.SaveChangesAsync(ct);
                return Resp<MediaItem>.Fail("no code found for item", RespCodes.NotFound);
            }
        }
        else
        {
            var results = await provider.SearchAsync(item.Title, item.Year, item.Type, language, ct);
            var best = PickBest(item, results);
            if (best is null)
            {
                item.Status = MediaItemStatus.Unmatched;
                item.UpdatedAt = _time.GetUtcNow();
                await _db.SaveChangesAsync(ct);
                return Resp<MediaItem>.Fail("no metadata match found", RespCodes.NotFound);
            }
            providerId = best.Id;
        }

        var meta = await provider.GetAsync(providerId, item.Type, language, ct);
        if (meta is null) return Resp<MediaItem>.Fail("metadata detail unavailable", 502);

        await ApplyAsync(item, meta, provider, ct);
        return Resp<MediaItem>.Ok(item);
    }

    /// <summary>
    /// 选择刮削 provider：显式指定优先（未知名字返回 null）；省略时若条目已有番号
    /// 且 code provider 已配置则走 code，否则走默认（TMDB）。
    /// </summary>
    private async Task<IMetadataProvider?> PickProviderAsync(MediaItem item, string? providerName, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(providerName))
            return string.Equals(providerName, "code", StringComparison.OrdinalIgnoreCase) ? _codeProvider
                : string.Equals(providerName, _provider.Name, StringComparison.OrdinalIgnoreCase) ? _provider
                : null;
        if (_codeProvider is not null && !string.IsNullOrWhiteSpace(item.Code) && await _codeProvider.IsConfiguredAsync(ct))
            return _codeProvider;
        return _provider;
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

        // 人工指定匹配属于明确的覆盖意图：即使条目已有 local 元数据也全量写入。
        await ApplyCoreAsync(_db, _fs, _provider, _time, item, meta, overwrite: true, ct);
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

        var startPending = await _db.MediaItems
            .CountAsync(i => i.LibraryId == libraryId && i.Status == MediaItemStatus.Pending, ct);

        // 空批次早退：没有待刮削条目时启动一轮「什么都不做」的任务，界面会显示
        // 「已在刮削」却永远不动。管理台据此把按钮置灰，这里是服务端兜底。
        if (startPending == 0)
            return Resp.Fail("no pending items to scrape", RespCodes.BadRequest);

        // 守卫必须**同步**登记在返回之前：放到 Task.Run 里的话，两个并发请求都能通过。
        if (!_scrape.TryBegin(libraryId, startPending))
            return Resp.Fail("scrape already running", RespCodes.Conflict);

        var run = _scrape.Get(libraryId);

        // 后台批量刮削：每条之间留出间隔，避免触发 provider 频率限制。
        // 批量任务固定走默认 provider（TMDB）；番号内容请对单条目调用
        // POST /items/{id}/scrape?provider=code，或保持 local 元数据不被覆盖。
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var sp = scope.ServiceProvider;
                var db = sp.GetRequiredService<OpenListDbContext>();
                var fs = sp.GetRequiredService<IFileSystemService>();
                // 批量任务固定走 TMDB：多 provider 注册后单个解析会取到最后一个，必须按名挑。
                var provider = sp.GetServices<IMetadataProvider>()
                    .FirstOrDefault(p => string.Equals(p.Name, "tmdb", StringComparison.OrdinalIgnoreCase))
                    ?? sp.GetRequiredService<IMetadataProvider>();
                var settings = sp.GetRequiredService<ISettingService>();

                var pending = await db.MediaItems
                    .Where(i => i.LibraryId == libraryId && i.Status == MediaItemStatus.Pending)
                    .OrderBy(i => i.Id)
                    .Take(MediaScrapeCoordinator.MaxBatch)
                    .ToListAsync(CancellationToken.None);

                var language = library.MetadataLanguage;
                if (string.IsNullOrWhiteSpace(language))
                {
                    var langResp = await settings.GetAsync("media_metadata_language", CancellationToken.None);
                    language = langResp.Code == 200 && !string.IsNullOrWhiteSpace(langResp.Data?.Value) ? langResp.Data!.Value : "zh-CN";
                }

                foreach (var item in pending)
                {
                    // 连续失败达到阈值就收工：API key 失效时每条都会抛，不熔断的话整批
                    // 会在 8 分钟里静默跑完，什么都没写，界面还以为在正常推进。
                    if (run is not null && run.ShouldAbort) break;

                    try
                    {
                        var results = await provider.SearchAsync(item.Title, item.Year, item.Type, language, CancellationToken.None);
                        var best = PickBest(item, results);
                        if (best is null)
                        {
                            // provider 正常响应、只是没有匹配项 —— 这不算失败，
                            // 否则冷门库连撞 5 条不匹配就会被误伤熔断。
                            item.Status = MediaItemStatus.Unmatched;
                            await db.SaveChangesAsync(CancellationToken.None);
                            run?.RecordSuccess();
                            await Task.Delay(250, CancellationToken.None);
                            continue;
                        }
                        var meta = await provider.GetAsync(best.Id, item.Type, language, CancellationToken.None);
                        if (meta is null)
                        {
                            run?.RecordFailure($"provider returned no detail for {best.Id}");
                            await Task.Delay(250, CancellationToken.None);
                            continue;
                        }

                        // 批量刮削只挑 Pending 条目（local 条目已是 Matched 不会进来），按覆盖处理。
                        await ApplyCoreAsync(db, fs, provider, _time, item, meta, overwrite: true, CancellationToken.None);
                        run?.RecordSuccess();
                    }
                    catch (Exception ex)
                    {
                        // 单条失败不影响整批；条目保持 Pending 以便下次重试。
                        run?.RecordFailure(ex.Message);
                    }
                    await Task.Delay(250, CancellationToken.None);
                }
            }
            finally
            {
                // 无论如何都要摘掉守卫，否则这一库会永远停在「正在刮削」。
                _scrape.End(libraryId);
            }
        }, CancellationToken.None);

        return Resp.Ok();
    }

    /// <inheritdoc />
    public async Task<Resp<List<MediaScrapeStatus>>> ListScrapeTasksAsync(User user, CancellationToken ct = default)
    {
        if (!user.CanManageMediaLibrary()) return Resp<List<MediaScrapeStatus>>.Fail("permission denied", 403);

        var libraries = await _db.MediaLibraries.AsNoTracking().OrderBy(l => l.Id).ToListAsync(ct);

        // 一次 GROUP BY 取全部库的三种状态计数，避免逐库查询。
        var counts = await _db.MediaItems.AsNoTracking()
            .GroupBy(i => new { i.LibraryId, i.Status })
            .Select(g => new { g.Key.LibraryId, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);
        var byLibrary = counts.GroupBy(c => c.LibraryId).ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<MediaScrapeStatus>(libraries.Count);
        foreach (var lib in libraries)
        {
            // 有 manage_media 但 BasePath 受限的委派管理员只能看到自己管得到的库。
            if (!_libraries.CanSeeLibrary(user, lib)) continue;

            var rows = byLibrary.GetValueOrDefault(lib.Id) ?? [];
            int CountOf(MediaItemStatus status) => rows.Where(r => r.Status == status).Sum(r => r.Count);

            var pending = CountOf(MediaItemStatus.Pending);
            var run = _scrape.Get(lib.Id);
            // 结束的一轮会保留记录（为了带上失败原因），所以「在跑」看的是 Running 而不是记录是否存在。
            var running = run?.Running == true;
            result.Add(new MediaScrapeStatus
            {
                LibraryId = lib.Id,
                LibraryName = lib.Name,
                Pending = pending,
                Unmatched = CountOf(MediaItemStatus.Unmatched),
                Matched = CountOf(MediaItemStatus.Matched),
                Running = running,
                // 批次数只在运行中有意义：结束后 pending 还会被别的路径改动，
                // 继续算差值会让进度在界面上倒着走。
                BatchSize = running ? run!.BatchSize : 0,
                BatchDone = running ? run!.DoneOf(pending) : 0,
                Failed = run?.Failed ?? 0,
                Error = run?.Error,
            });
        }
        return Resp<List<MediaScrapeStatus>>.Ok(result);
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
    private async Task ApplyAsync(MediaItem item, MediaMetadata meta, IMetadataProvider provider, CancellationToken ct)
        => await ApplyCoreAsync(_db, _fs, provider, _time, item, meta,
            overwrite: !string.Equals(item.MetadataProvider, "local", StringComparison.OrdinalIgnoreCase), ct);

    /// <summary>
    /// 元数据落库 + 落盘的核心实现（静态以便批量任务复用独立作用域）。
    /// <paramref name="overwrite"/> 为 false 时进入「只补缺」模式（NFO 双向化的另一半）：
    /// 元数据来源为 local（扫描期 NFO/图片识别）的条目只补空缺字段与缺失的演职员/图片，
    /// 不覆盖本地已有的信息；此时也不改写 MetadataProvider —— 本地元数据仍是事实来源，
    /// TMDB 只是补齐外部 id 与缺漏。
    /// </summary>
    private static async Task ApplyCoreAsync(
        OpenListDbContext db, IFileSystemService fs, IMetadataProvider provider, TimeProvider time,
        MediaItem item, MediaMetadata meta, bool overwrite, CancellationToken ct)
    {
        bool Missing(string? s) => string.IsNullOrWhiteSpace(s);
        if (overwrite)
        {
            item.Title = string.IsNullOrWhiteSpace(meta.Title) ? item.Title : meta.Title;
            item.OriginalTitle = meta.OriginalTitle;
            item.Overview = meta.Overview;
            item.Rating = meta.Rating;
            if (meta.Genres.Count > 0) item.SetGenres(meta.Genres);
        }
        else
        {
            if (Missing(item.Title) && !string.IsNullOrWhiteSpace(meta.Title)) item.Title = meta.Title;
            if (Missing(item.OriginalTitle)) item.OriginalTitle = meta.OriginalTitle;
            if (Missing(item.Overview)) item.Overview = meta.Overview;
            if (item.Rating is null) item.Rating = meta.Rating;
            if (item.Genres.Count == 0 && meta.Genres.Count > 0) item.SetGenres(meta.Genres);
        }
        item.Year ??= meta.Year;
        if (string.Equals(provider.Name, "code", StringComparison.OrdinalIgnoreCase))
        {
            // 番号 provider：meta.Id 是番号本体，写入 Code 而不是 TmdbId；
            // 片商/系列也只由该 provider 维护（TMDB 结果不含这两个字段，不触碰既有关联）。
            if (overwrite || Missing(item.Code)) item.Code = meta.Id;
            var now = time.GetUtcNow();
            if (overwrite || item.StudioId is null)
                item.StudioId = string.IsNullOrWhiteSpace(meta.Studio)
                    ? overwrite ? null : item.StudioId
                    : await MediaTaxonomySync.ResolveStudioAsync(db, now, meta.Studio, ct);
            if (overwrite || item.SeriesId is null)
                item.SeriesId = string.IsNullOrWhiteSpace(meta.Series)
                    ? overwrite ? null : item.SeriesId
                    : await MediaTaxonomySync.ResolveSeriesAsync(db, now, meta.Series, ct);
        }
        else
        {
            item.TmdbId = meta.Id;
        }
        item.Status = MediaItemStatus.Matched;
        if (overwrite) item.MetadataProvider = provider.Name;
        item.UpdatedAt = time.GetUtcNow();

        var dir = NfoDirectory(item);

        // 图片：先下载到内存，写回成功后才记录路径；只补缺模式下本地已有图片则跳过下载。
        if (overwrite || Missing(item.PosterPath))
        {
            if (!string.IsNullOrWhiteSpace(meta.PosterPath))
            {
                var bytes = await provider.GetImageAsync(meta.PosterPath, ct);
                if (bytes is not null && await WriteFileAsync(fs, dir + "/poster.jpg", bytes, ct))
                    item.PosterPath = dir + "/poster.jpg";
            }
        }
        if (overwrite || Missing(item.BackdropPath))
        {
            if (!string.IsNullOrWhiteSpace(meta.BackdropPath))
            {
                var bytes = await provider.GetImageAsync(meta.BackdropPath, ct);
                if (bytes is not null && await WriteFileAsync(fs, dir + "/fanart.jpg", bytes, ct))
                    item.BackdropPath = dir + "/fanart.jpg";
            }
        }

        await UpsertPersonsAsync(db, time, item, meta, overwrite, ct);
        // 覆盖模式直接写回；只补缺模式条目可能自带手写 NFO（local 事实来源），
        // 仅当目标 NFO 不存在时才补写，避免抹掉用户的本地数据。
        if (overwrite || !await NfoExistsAsync(fs, item, dir, ct))
            await WriteNfoAsync(fs, item, meta, dir, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>检查条目目标位置的 NFO 是否已存在（maxBytes=1 的探测读取：存在但超限返回 BadRequest）。</summary>
    private static async Task<bool> NfoExistsAsync(IFileSystemService fs, MediaItem item, string dir, CancellationToken ct)
    {
        var read = await fs.ReadFileAsync(dir + "/" + NfoFileName(item) + ".nfo", maxBytes: 1, WriterUser, ct);
        return read.Code is RespCodes.Ok or RespCodes.BadRequest;
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
        // uniqueid 的 type 跟随来源：code provider 写 type=code（番号本体）。
        var idType = string.Equals(item.MetadataProvider, "code", StringComparison.OrdinalIgnoreCase) ? "code" : "tmdb";
        root.Add(new XElement("uniqueid", new XAttribute("type", idType), new XAttribute("default", "true"), meta.Id));
        // 番号域的片商/系列随 NFO 落盘：Kodi/Jellyfin 认 <studio> 与 <set>。
        if (!string.IsNullOrWhiteSpace(meta.Studio)) root.Add(new XElement("studio", meta.Studio));
        if (!string.IsNullOrWhiteSpace(meta.Series)) root.Add(new XElement("set", meta.Series));

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

    /// <summary>写入创作者关联：覆盖模式重建名单；只补缺模式在条目已有任何关联时跳过。</summary>
    private static async Task UpsertPersonsAsync(
        OpenListDbContext db, TimeProvider time, MediaItem item, MediaMetadata meta, bool overwrite, CancellationToken ct)
    {
        if (!overwrite)
        {
            var hasLinks = await db.MediaItemPersons.AnyAsync(r => r.ItemId == item.Id, ct);
            if (hasLinks) return;
        }
        await MediaPersonSync.ReplaceAsync(db, time, item.Id,
            meta.Persons.Select(p => new MediaPersonSync.Entry(
                p.Name, p.ProviderId, p.Role, p.Character, p.ProfilePath, p.Order)), ct);
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
