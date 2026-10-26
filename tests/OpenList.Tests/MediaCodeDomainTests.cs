using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Media.Domain;
using OpenList.Media.Domain.Abstractions;
using OpenList.Media.Providers;
using OpenList.Media.Services;

namespace OpenList.Tests;

/// <summary>
/// 番号域回归：番号解析、NFO 片商/系列解析、code provider 的宽松 JSON 映射、
/// 扫描器对 Code 的落库与回填、片商/系列取或建与聚合查询。
/// </summary>
public class MediaCodeDomainTests
{
    // ------------------------------------------------------------------
    // MediaNaming.ExtractCode
    // ------------------------------------------------------------------

    [Test]
    public async Task ExtractCode_Hyphenated_Form()
    {
        await Assert.That(MediaNaming.ExtractCode("MIDE-883")).IsEqualTo("MIDE-883");
        await Assert.That(MediaNaming.ExtractCode("ssis-007")).IsEqualTo("SSIS-007");
        await Assert.That(MediaNaming.ExtractCode("IPX-001-4K.mp4")).IsEqualTo("IPX-001");
        await Assert.That(MediaNaming.ExtractCode("abp-123-cd2")).IsEqualTo("ABP-123");
    }

    [Test]
    public async Task ExtractCode_Bracketed_Form_Takes_Priority()
    {
        await Assert.That(MediaNaming.ExtractCode("[MIDE-883] Some Title")).IsEqualTo("MIDE-883");
        await Assert.That(MediaNaming.ExtractCode("(t28-621) ok")).IsEqualTo("T28-621");
        await Assert.That(MediaNaming.ExtractCode("【ID550-123】x")).IsEqualTo("ID550-123");
    }

    [Test]
    public async Task ExtractCode_Joined_Form()
    {
        await Assert.That(MediaNaming.ExtractCode("MIDE883")).IsEqualTo("MIDE-883");
        await Assert.That(MediaNaming.ExtractCode("abp123 movie.mp4")).IsEqualTo("ABP-123");
    }

    [Test]
    public async Task ExtractCode_Fc2_Form()
    {
        await Assert.That(MediaNaming.ExtractCode("FC2-PPV-1234567")).IsEqualTo("FC2-PPV-1234567");
        await Assert.That(MediaNaming.ExtractCode("fc2ppv_1234567")).IsEqualTo("FC2-PPV-1234567");
        await Assert.That(MediaNaming.ExtractCode("FC2-1234567.mp4")).IsEqualTo("FC2-1234567");
    }

    [Test]
    public async Task ExtractCode_Rejects_Years_And_NonCodes()
    {
        await Assert.That(MediaNaming.ExtractCode("Blade2049")).IsNull();
        await Assert.That(MediaNaming.ExtractCode("Star-Trek-2009")).IsNull();
        await Assert.That(MediaNaming.ExtractCode("Some Movie 1080p")).IsNull();
        await Assert.That(MediaNaming.ExtractCode("Season 1")).IsNull();
        await Assert.That(MediaNaming.ExtractCode("Just A Title")).IsNull();
    }

    // ------------------------------------------------------------------
    // MediaNfoReader 的 studio / series
    // ------------------------------------------------------------------

    [Test]
    public async Task Nfo_Studio_And_Set_With_Name_Child()
    {
        var nfo = MediaNfoReader.Parse("""
            <movie>
              <title>Sample</title>
              <studio>Prestige</studio>
              <set><name>Collectors</name><overview>box</overview></set>
            </movie>
            """);
        await Assert.That(nfo).IsNotNull();
        await Assert.That(nfo!.Studio).IsEqualTo("Prestige");
        await Assert.That(nfo.Series).IsEqualTo("Collectors");
    }

    [Test]
    public async Task Nfo_Series_Takes_Priority_Over_Plain_Set()
    {
        var nfo = MediaNfoReader.Parse("""
            <movie>
              <title>Sample</title>
              <series>S1</series>
              <set>Ignored</set>
            </movie>
            """);
        await Assert.That(nfo!.Series).IsEqualTo("S1");
    }

    [Test]
    public async Task Nfo_Plain_Set_Text_Fallback()
    {
        var nfo = MediaNfoReader.Parse("""
            <movie>
              <title>Sample</title>
              <set>PlainSet</set>
            </movie>
            """);
        await Assert.That(nfo!.Series).IsEqualTo("PlainSet");
    }

    [Test]
    public async Task Nfo_Only_Studio_Is_Not_Empty_Metadata()
    {
        // 只有 studio 也应产出结果（不被"内容为空"判定吞掉）。
        var nfo = MediaNfoReader.Parse("<movie><studio>Prestige</studio></movie>");
        await Assert.That(nfo).IsNotNull();
    }

    // ------------------------------------------------------------------
    // CodeMetadataProvider
    // ------------------------------------------------------------------

    [Test]
    public async Task CodeProvider_Search_Parses_Code_From_Title()
    {
        var provider = new CodeMetadataProvider(FakeHttp.Fail(), new FakeSettings(new Dictionary<string, string>()));
        var results = await provider.SearchAsync("[MIDE-883] whatever", null, MediaLibraryType.Movie, "zh-CN");
        await Assert.That(results).HasCount().EqualTo(1);
        await Assert.That(results[0].Id).IsEqualTo("MIDE-883");

        var none = await provider.SearchAsync("Random Movie", null, MediaLibraryType.Movie, "zh-CN");
        await Assert.That(none).HasCount().EqualTo(0);
    }

    [Test]
    public async Task CodeProvider_Not_Configured_Without_Setting()
    {
        var provider = new CodeMetadataProvider(FakeHttp.Fail(), new FakeSettings(new Dictionary<string, string>()));
        await Assert.That(await provider.IsConfiguredAsync()).IsFalse();

        var configured = new CodeMetadataProvider(FakeHttp.Fail(), new FakeSettings(new Dictionary<string, string>
        {
            [CodeMetadataProvider.ApiUrlSetting] = "https://example.com/api/{code}",
        }));
        await Assert.That(await configured.IsConfiguredAsync()).IsTrue();
    }

    [Test]
    public async Task CodeProvider_Get_Maps_Loose_Json()
    {
        var json = """
            {
              "title": "Sample Title",
              "original_title": "サンプル",
              "year": "2023-05-01",
              "overview": "plot text",
              "rating": 4.5,
              "studio": "Prestige",
              "series": "Collectors",
              "genres": ["Drama", " Action "],
              "poster": "https://img.example.com/p.jpg",
              "actors": [
                { "name": "Alice", "character": "Hero" },
                "Bob"
              ]
            }
            """;
        var provider = new CodeMetadataProvider(
            FakeHttp.RespondingWith(json, "application/json"),
            new FakeSettings(new Dictionary<string, string>
            {
                [CodeMetadataProvider.ApiUrlSetting] = "https://example.com/api/{code}",
            }));

        var meta = await provider.GetAsync("MIDE-883", MediaLibraryType.Movie, "zh-CN");
        await Assert.That(meta).IsNotNull();
        await Assert.That(meta!.Id).IsEqualTo("MIDE-883");
        await Assert.That(meta.Title).IsEqualTo("Sample Title");
        await Assert.That(meta.OriginalTitle).IsEqualTo("サンプル");
        await Assert.That(meta.Year).IsEqualTo(2023);
        await Assert.That(meta.Rating).IsEqualTo(4.5);
        await Assert.That(meta.Studio).IsEqualTo("Prestige");
        await Assert.That(meta.Series).IsEqualTo("Collectors");
        await Assert.That(meta.PosterPath).IsEqualTo("https://img.example.com/p.jpg");
        await Assert.That(meta.Genres).IsEquivalentTo(["Drama", "Action"]);
        await Assert.That(meta.Persons).HasCount().EqualTo(2);
        await Assert.That(meta.Persons[0].Name).IsEqualTo("Alice");
        await Assert.That(meta.Persons[0].Character).IsEqualTo("Hero");
        await Assert.That(meta.Persons[1].Name).IsEqualTo("Bob");
        await Assert.That(meta.Persons[1].Character).IsNull();
    }

    [Test]
    public async Task CodeProvider_Get_Returns_Null_When_Unconfigured()
    {
        var provider = new CodeMetadataProvider(FakeHttp.Fail(), new FakeSettings(new Dictionary<string, string>()));
        await Assert.That(await provider.GetAsync("MIDE-883", MediaLibraryType.Movie, "zh-CN")).IsNull();
    }

    // ------------------------------------------------------------------
    // 扫描器：Code 落库与存量回填；片商/系列取或建
    // ------------------------------------------------------------------

    [Test]
    public async Task Scanner_Fills_Code_On_Create_And_Backfills_Existing()
    {
        await using var ctx = BuildContext();
        var scanner = new MediaLibraryScanner(fs: null!);
        var now = DateTimeOffset.UtcNow;

        var discovered = new List<MediaFileCandidate>
        {
            new("/f/media/MIDE-883/movie.mp4", 1, now),
            new("/f/media/Random Movie/movie.mp4", 1, now),
        };
        await scanner.PersistAsync(ctx, 1, "/f/media", MediaLibraryType.Mixed, discovered, now, ct: default);

        var withCode = await ctx.MediaItems.SingleAsync(i => i.DirectoryPath == "/f/media/MIDE-883");
        await Assert.That(withCode.Code).IsEqualTo("MIDE-883");
        var withoutCode = await ctx.MediaItems.SingleAsync(i => i.DirectoryPath == "/f/media/Random Movie");
        await Assert.That(withoutCode.Code).IsNull();

        // 存量回填：模拟番号功能上线前的旧条目（Code 为空），重扫时幂等补上。
        ctx.MediaItems.Add(new MediaItem
        {
            LibraryId = 1,
            Type = MediaLibraryType.Movie,
            Title = "Legacy",
            DirectoryPath = "/f/media/SSIS-007",
            Status = MediaItemStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await ctx.SaveChangesAsync();

        var legacyScan = new List<MediaFileCandidate> { new("/f/media/SSIS-007/movie.mp4", 1, now) };
        await scanner.PersistAsync(ctx, 1, "/f/media", MediaLibraryType.Mixed, legacyScan, now, ct: default);
        var backfilled = await ctx.MediaItems.SingleAsync(i => i.DirectoryPath == "/f/media/SSIS-007");
        await Assert.That(backfilled.Code).IsEqualTo("SSIS-007");
    }

    [Test]
    public async Task TaxonomySync_Resolve_Is_Idempotent_Per_Name()
    {
        await using var ctx = BuildContext();
        var now = DateTimeOffset.UtcNow;

        var studio1 = await MediaTaxonomySync.ResolveStudioAsync(ctx, now, "Prestige", default);
        var studio2 = await MediaTaxonomySync.ResolveStudioAsync(ctx, now, "Prestige", default);
        var other = await MediaTaxonomySync.ResolveStudioAsync(ctx, now, "Other", default);
        await Assert.That(studio1).IsEqualTo(studio2);
        await Assert.That(studio1).IsNotEqualTo(other);
        await Assert.That(await ctx.MediaStudios.CountAsync()).IsEqualTo(2);

        var series1 = await MediaTaxonomySync.ResolveSeriesAsync(ctx, now, "Collectors", default);
        var series2 = await MediaTaxonomySync.ResolveSeriesAsync(ctx, now, "Collectors", default);
        await Assert.That(series1).IsEqualTo(series2);
        await Assert.That(await ctx.MediaSeries.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task Catalog_ListStudios_Counts_Only_Visible_Items()
    {
        await using var ctx = BuildContext();
        var now = DateTimeOffset.UtcNow;
        var studioId = await MediaTaxonomySync.ResolveStudioAsync(ctx, now, "Prestige", default);
        ctx.MediaItems.AddRange(
            new MediaItem
            {
                LibraryId = 1, Type = MediaLibraryType.Movie, Title = "A",
                StudioId = studioId, Status = MediaItemStatus.Matched,
                DirectoryPath = "/f/a", CreatedAt = now, UpdatedAt = now,
            },
            new MediaItem
            {
                LibraryId = 1, Type = MediaLibraryType.Movie, Title = "B",
                StudioId = studioId, Status = MediaItemStatus.Matched,
                DirectoryPath = "/f/b", CreatedAt = now, UpdatedAt = now,
            },
            new MediaItem
            {
                LibraryId = 1, Type = MediaLibraryType.Movie, Title = "C",
                Status = MediaItemStatus.Matched,
                DirectoryPath = "/f/c", CreatedAt = now, UpdatedAt = now,
            });
        await ctx.SaveChangesAsync();

        var catalog = new MediaCatalogService(ctx, new FakeLibraryService());
        var resp = await catalog.ListStudiosAsync(null, 1, 30, AdminUser);
        await Assert.That(resp.Code).IsEqualTo(200);
        var rows = resp.Data!;
        await Assert.That(rows.Total).IsEqualTo(1);
        await Assert.That(rows.Content[0].Name).IsEqualTo("Prestige");
        await Assert.That(rows.Content[0].ItemCount).IsEqualTo(2);

        var items = await catalog.ListStudioItemsAsync(studioId, 1, 30, AdminUser);
        await Assert.That(items.Data!.Total).IsEqualTo(2);

        var missing = await catalog.ListStudioItemsAsync(999, 1, 30, AdminUser);
        await Assert.That(missing.Code).IsEqualTo(404);
    }

    private static readonly User AdminUser = new() { Id = 1, Username = "admin", Role = UserRole.Admin };

    private static OpenListDbContext BuildContext()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<OpenListDbContext>()
            .UseSqlite(conn)
            .Options;
        var ctx = new OpenListDbContext(opts, new DatabaseConfig());
        ctx.Database.EnsureCreated();
        return ctx;
    }

    // ------------------------------------------------------------------
    // 测试替身
    // ------------------------------------------------------------------

    /// <summary>ISettingService 假件：按字典返回（缺失的键返回 404），满足 code provider 的设置读取。</summary>
    private sealed class FakeSettings(Dictionary<string, string> values) : ISettingService
    {
        public Task<Resp<List<SettingItem>>> ListAsync(CancellationToken ct = default)
            => Task.FromResult(Resp<List<SettingItem>>.Ok([.. values.Select(kv => new SettingItem { Key = kv.Key, Value = kv.Value })]));

        public Task<Resp<SettingItem>> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(values.TryGetValue(key, out var v)
                ? Resp<SettingItem>.Ok(new SettingItem { Key = key, Value = v })
                : Resp<SettingItem>.Fail("not found", RespCodes.NotFound));

        public Task<Resp> SaveAsync(List<SettingItem> items, CancellationToken ct = default) => Task.FromResult(Resp.Ok());
        public Task<Resp> DeleteAsync(string key, CancellationToken ct = default) => Task.FromResult(Resp.Ok());
        public Task<Resp<List<SettingItem>>> DefaultAsync(string? group = null, CancellationToken ct = default)
            => Task.FromResult(Resp<List<SettingItem>>.Ok([]));
        public Task<Resp> ResetTokenAsync(CancellationToken ct = default) => Task.FromResult(Resp.Ok());
        public Task<Resp<Dictionary<string, string>>> GetPublicAsync(CancellationToken ct = default)
            => Task.FromResult(Resp<Dictionary<string, string>>.Ok([]));
        public Task<Resp<string[]>> GetOfflineDownloadToolsAsync(CancellationToken ct = default)
            => Task.FromResult(Resp<string[]>.Ok([]));
        public Task<Resp<string[]>> GetArchiveExtensionsAsync(CancellationToken ct = default)
            => Task.FromResult(Resp<string[]>.Ok([]));
    }

    /// <summary>IHttpClientFactory 假件。</summary>
    private static class FakeHttp
    {
        /// <summary>固定返回一个响应体（每次 CreateClient 都给新的 handler，避免响应对象被复用后失效）。</summary>
        public static IHttpClientFactory RespondingWith(string json, string contentType)
        {
            var content = new StringContent(json, Encoding.UTF8, contentType);
            return new StubFactory(new StubHandler(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content }));
        }

        /// <summary>永远返回 500（用于"未配置/失败"路径，请求不会真的发出）。</summary>
        public static IHttpClientFactory Fail() => new StubFactory(new StubHandler(
            new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)));

        private sealed class StubFactory(StubHandler handler) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
        }

        private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(response);
        }
    }

    /// <summary>IMediaLibraryService 假件：仅满足目录聚合服务的库可见性查询（返回一个全可见的管理员库）。</summary>
    private sealed class FakeLibraryService : IMediaLibraryService
    {
        public Task<Resp<MediaLibrary>> CreateAsync(CreateMediaLibraryReq req, User user, CancellationToken ct = default)
            => Task.FromResult(Resp<MediaLibrary>.Fail("not implemented", 500));
        public Task<Resp> UpdateAsync(long id, UpdateMediaLibraryReq req, User user, CancellationToken ct = default)
            => Task.FromResult(Resp.Fail("not implemented", 500));
        public Task<Resp> DeleteAsync(long id, User user, CancellationToken ct = default)
            => Task.FromResult(Resp.Fail("not implemented", 500));
        public Task<Resp<List<MediaLibrary>>> ListAsync(User user, CancellationToken ct = default)
            => Task.FromResult(Resp<List<MediaLibrary>>.Ok([new MediaLibrary { Id = 1, Name = "lib", Path = "/f" }]));
        public Task<Resp<MediaLibrary>> GetAsync(long id, User user, CancellationToken ct = default)
            => Task.FromResult(Resp<MediaLibrary>.Fail("not implemented", 500));
        public Task<Resp<PageResp<MediaItem>>> ListItemsAsync(MediaItemQuery query, User user, CancellationToken ct = default)
            => Task.FromResult(Resp<PageResp<MediaItem>>.Fail("not implemented", 500));
        public Task<Resp<MediaItem>> GetItemAsync(long itemId, User user, CancellationToken ct = default)
            => Task.FromResult(Resp<MediaItem>.Fail("not implemented", 500));
        public Task<Resp<List<MediaFile>>> ListItemFilesAsync(long itemId, User user, CancellationToken ct = default)
            => Task.FromResult(Resp<List<MediaFile>>.Fail("not implemented", 500));
        public bool CanSeeLibrary(User user, MediaLibrary library) => true;
        public bool CanSeeItem(User user, MediaItem item) => true;
    }
}
