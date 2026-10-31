using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;
using OpenList.Infrastructure.Thumbnails;
using OpenList.Vfs;

namespace OpenList.Tests;

/// <summary>
/// 缩略图服务（<c>/p?type=thumb</c>）的单元测试。
/// <para>
/// 覆盖的都是「不依赖外部 ffmpeg 也能确定结果」的部分：缓存键、类型判定、参数拼装、
/// 设置项映射、URL 下发条件，以及 ffmpeg 缺失时的确定性降级。
/// 真正的像素级生成由 E2E 覆盖（那里回归脚本已把 ffmpeg 放进 PATH）。
/// </para>
/// </summary>
public class ThumbnailTests
{
    // ---- 缓存键 ----

    [Test]
    public async Task BuildKey_StableForSameInput()
    {
        var a = ThumbnailService.BuildKey("/a/b.jpg", 1024, 12345, 320, 320, "jpg", 80, "");
        var b = ThumbnailService.BuildKey("/a/b.jpg", 1024, 12345, 320, 320, "jpg", 80, "");
        await Assert.That(a).IsEqualTo(b);
        await Assert.That(a.Length).IsEqualTo(40); // SHA1 十六进制
    }

    [Test]
    public async Task BuildKey_ChangesWithEveryDimension()
    {
        var baseKey = ThumbnailService.BuildKey("/a/b.jpg", 1024, 12345, 320, 320, "jpg", 80, "");
        var variants = new[]
        {
            ThumbnailService.BuildKey("/a/c.jpg", 1024, 12345, 320, 320, "jpg", 80, ""),  // path
            ThumbnailService.BuildKey("/a/b.jpg", 2048, 12345, 320, 320, "jpg", 80, ""),   // size
            ThumbnailService.BuildKey("/a/b.jpg", 1024, 99999, 320, 320, "jpg", 80, ""),   // mtime
            ThumbnailService.BuildKey("/a/b.jpg", 1024, 12345, 640, 320, "jpg", 80, ""),   // width
            ThumbnailService.BuildKey("/a/b.jpg", 1024, 12345, 320, 640, "jpg", 80, ""),   // height
            ThumbnailService.BuildKey("/a/b.jpg", 1024, 12345, 320, 320, "webp", 80, ""),  // format
            ThumbnailService.BuildKey("/a/b.jpg", 1024, 12345, 320, 320, "jpg", 60, ""),   // quality
            ThumbnailService.BuildKey("/a/b.jpg", 1024, 12345, 320, 320, "jpg", 80, "35"), // seek position
        };
        foreach (var variant in variants)
            await Assert.That(variant).IsNotEqualTo(baseKey);
    }

    [Test]
    public async Task ContentTypeOfBytes_SniffsActualFormat()
    {
        // 请求 webp 但 ffmpeg 不支持 → 落成 jpeg：Content-Type 必须跟着实际字节走，
        // 否则浏览器按 webp 解析 JPEG 会得到裂图。
        var webp = new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P', 0 };
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D };

        await Assert.That(ThumbnailService.ContentTypeOfBytes(webp, "jpg")).IsEqualTo("image/webp");
        await Assert.That(ThumbnailService.ContentTypeOfBytes(jpeg, "webp")).IsEqualTo("image/jpeg");
        await Assert.That(ThumbnailService.ContentTypeOfBytes(png, "webp")).IsEqualTo("image/png");
        await Assert.That(ThumbnailService.ContentTypeOfBytes([1, 2, 3], "png")).IsEqualTo("image/png");
    }

    // ---- 类型判定 ----

    [Test]
    public async Task ThumbKinds_ClassifiesByExtension()
    {
        await Assert.That(ThumbKinds.FromName("photo.JPG")).IsEqualTo(ThumbKind.Image);
        await Assert.That(ThumbKinds.FromName("song.flac")).IsEqualTo(ThumbKind.Audio);
        await Assert.That(ThumbKinds.FromName("movie.mkv")).IsEqualTo(ThumbKind.Video);
        await Assert.That(ThumbKinds.FromName("book.pdf")).IsEqualTo(ThumbKind.Pdf);
        // svg 是存储型 XSS 载体，必须落在「不支持」。
        await Assert.That(ThumbKinds.FromName("icon.svg")).IsEqualTo(ThumbKind.None);
        await Assert.That(ThumbKinds.FromName("notes.txt")).IsEqualTo(ThumbKind.None);
        await Assert.That(ThumbKinds.FromName("noext")).IsEqualTo(ThumbKind.None);
    }

    [Test]
    public async Task ThumbKinds_VideoIsNotListSafe()
    {
        await Assert.That(ThumbKinds.IsListSafe(ThumbKind.Image)).IsTrue();
        await Assert.That(ThumbKinds.IsListSafe(ThumbKind.Audio)).IsTrue();
        // 一个 20 个视频的目录 = 20 次远端抽帧，列表页绝不下发。
        await Assert.That(ThumbKinds.IsListSafe(ThumbKind.Video)).IsFalse();
    }

    // ---- ffmpeg 参数 ----

    [Test]
    public async Task BuildArguments_PutsFilterAsSingleArgument()
    {
        var args = ThumbnailService.BuildArguments(ThumbKind.Image, "in.jpg", "out.jpg", 320, 240, "jpg", 80, "");

        var index = args.IndexOf("-vf");
        await Assert.That(index).IsGreaterThanOrEqualTo(0);
        // 关键：整条滤镜链是一个 argv 元素。拼成字符串会被子进程按空格拆散（Jellyfin #18322）。
        await Assert.That(args[index + 1])
            .IsEqualTo("scale=320:240:force_original_aspect_ratio=increase:flags=lanczos,crop=320:240");
        await Assert.That(args[index + 1].Contains(' ')).IsFalse();
    }

    [Test]
    public async Task BuildArguments_SeeksBeforeInput_ForVideo()
    {
        var args = ThumbnailService.BuildArguments(ThumbKind.Video, "/media/a.mkv", "out.jpg", 320, 320, "jpg", 80, "12.5");

        var seek = args.IndexOf("-ss");
        var input = args.IndexOf("-i");
        await Assert.That(seek).IsGreaterThanOrEqualTo(0);
        await Assert.That(args[seek + 1]).IsEqualTo("12.5");
        // -ss 必须在 -i 之前：这是"快进"语义，只解关键帧，比解码整段快一到两个数量级。
        await Assert.That(seek).IsLessThan(input);
    }

    [Test]
    public async Task BuildArguments_UsesEmbeddedCoverStream_ForAudio()
    {
        var args = ThumbnailService.BuildArguments(ThumbKind.Audio, "/music/a.mp3", "out.jpg", 320, 320, "jpg", 80, "");

        var map = args.IndexOf("-map");
        await Assert.That(map).IsGreaterThanOrEqualTo(0);
        await Assert.That(args[map + 1]).IsEqualTo("0:v:0");
        await Assert.That(args.Contains("-frames:v")).IsTrue();
    }

    [Test]
    public async Task BuildArguments_UsesLibwebp_WhenFormatIsWebp()
    {
        var args = ThumbnailService.BuildArguments(ThumbKind.Image, "in.png", "out.webp", 320, 320, "webp", 75, "");

        var codec = args.IndexOf("-c:v");
        await Assert.That(args[codec + 1]).IsEqualTo("libwebp");
        var quality = args.IndexOf("-quality");
        await Assert.That(args[quality + 1]).IsEqualTo("75");
    }

    // ---- 设置项映射 ----

    [Test]
    public async Task Options_FromSettings_ClampsAndResolvesDefaults()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "openlist-thumb-tests");
        var dict = new Dictionary<string, string>
        {
            ["thumb_enable"] = "true",
            ["thumb_max_size"] = "9999",
            ["thumb_quality"] = "250",
            ["thumb_cache_folder"] = "",
        };

        var options = ThumbnailOptions.FromSettings(dict, dataDir);

        await Assert.That(options.Enabled).IsTrue();
        await Assert.That(options.MaxSize).IsEqualTo(1024);   // 夹到上限
        await Assert.That(options.Quality).IsEqualTo(100);    // 夹到上限
        await Assert.That(options.CacheFolder).IsEqualTo(Path.Combine(dataDir, "thumbnails"));
        // 缺键时的默认值：视频开、抽帧位置 10%。
        await Assert.That(options.VideoEnabled).IsTrue();
        await Assert.That(options.VideoPosition).IsEqualTo("10%");
    }

    [Test]
    public async Task Options_FromSettings_DefaultIsDisabled()
    {
        var options = ThumbnailOptions.FromSettings(new Dictionary<string, string>(), Path.GetTempPath());
        // 升级后不能凭空多出解码负载：缺键即关闭。
        await Assert.That(options.Enabled).IsFalse();
    }

    [Test]
    public async Task Options_FromSettings_ResolvesRelativeCacheFolder()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "openlist-thumb-tests");
        var options = ThumbnailOptions.FromSettings(
            new Dictionary<string, string> { ["thumb_cache_folder"] = "cache/thumbs" }, dataDir);

        await Assert.That(Path.IsPathRooted(options.CacheFolder)).IsTrue();
        await Assert.That(options.CacheFolder).IsEqualTo(Path.GetFullPath(Path.Combine(dataDir, "cache/thumbs")));
    }

    [Test]
    public async Task Options_AllowedExtensions_ParsesWhitelist()
    {
        var options = ThumbnailOptions.FromSettings(
            new Dictionary<string, string> { ["thumb_types"] = "jpg, .PNG , mp4" }, Path.GetTempPath());

        await Assert.That(options.AllowsExtension("jpg")).IsTrue();
        await Assert.That(options.AllowsExtension("png")).IsTrue();
        await Assert.That(options.AllowsExtension("webp")).IsFalse();
    }

    // ---- URL 下发 ----

    [Test]
    public async Task UrlBuilder_EmitsThumbUrl_ForImageAndAudio()
    {
        var builder = new ThumbnailUrlBuilder(() => EnabledOptions());

        var image = builder.Build("/test/a.jpg", "a.jpg", 1024, false, SignContext.NotNeeded);
        await Assert.That(image).IsNotNull();
        await Assert.That(image!).Contains("/p/test/a.jpg?type=thumb&w=320&h=320");

        var audio = builder.Build("/test/b.mp3", "b.mp3", 1024, false, SignContext.NotNeeded);
        await Assert.That(audio).IsNotNull();
    }

    [Test]
    public async Task UrlBuilder_SkipsVideoDirAndUnknown()
    {
        var builder = new ThumbnailUrlBuilder(() => EnabledOptions());

        // 列表/网格不对视频下发 thumb（否则一个目录就是 N 次远端抽帧）。
        await Assert.That(builder.Build("/test/a.mp4", "a.mp4", 1024, false, SignContext.NotNeeded)).IsNull();
        await Assert.That(builder.Build("/test/dir", "dir", 0, true, SignContext.NotNeeded)).IsNull();
        await Assert.That(builder.Build("/test/a.txt", "a.txt", 10, false, SignContext.NotNeeded)).IsNull();
        // 驱动级开关关掉时同样不下发。
        await Assert.That(builder.Build("/test/a.jpg", "a.jpg", 1024, false, SignContext.NotNeeded, driverAllowsThumb: false)).IsNull();
    }

    [Test]
    public async Task UrlBuilder_ReturnsNull_WhenDisabledOrTooLarge()
    {
        await Assert.That(new ThumbnailUrlBuilder(() => ThumbnailOptions.Disabled("/tmp"))
            .Build("/test/a.jpg", "a.jpg", 1024, false, SignContext.NotNeeded)).IsNull();

        var tooLarge = EnabledOptions() with { MaxSourceBytes = 100 };
        await Assert.That(new ThumbnailUrlBuilder(() => tooLarge)
            .Build("/test/a.jpg", "a.jpg", 1024, false, SignContext.NotNeeded)).IsNull();
    }

    [Test]
    public async Task UrlBuilder_AppendsSign_WhenRequired()
    {
        var builder = new ThumbnailUrlBuilder(() => EnabledOptions(), publicUrls: null, signs: new FakeSignService());

        var url = builder.Build("/test/a.jpg", "a.jpg", 1024, false, new SignContext(true, 0));
        await Assert.That(url!).Contains("&sign=");
    }

    // ---- 端到端（无 ffmpeg 也要有确定结果）----

    [Test]
    public async Task GetAsync_Disabled_ReturnsUnsupported()
    {
        var (service, _) = await SetupAsync(new Dictionary<string, string>());
        var result = await service.GetAsync(new ThumbnailRequest("/test/a.jpg"), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(ThumbnailStatus.Unsupported);
        await Assert.That(result.Reason).IsEqualTo("disabled");
    }

    [Test]
    public async Task GetAsync_UnsupportedType_ReturnsUnsupported()
    {
        var (service, _) = await SetupAsync(EnabledSettings());
        var result = await service.GetAsync(new ThumbnailRequest("/test/notes.txt"), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(ThumbnailStatus.Unsupported);
        await Assert.That(result.Reason).IsEqualTo("unsupported");
    }

    [Test]
    public async Task GetAsync_MissingFile_ReturnsNotFound()
    {
        var (service, _) = await SetupAsync(EnabledSettings());
        var result = await service.GetAsync(new ThumbnailRequest("/test/missing.jpg"), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(ThumbnailStatus.NotFound);
    }

    [Test]
    public async Task GetAsync_TooLargeSource_ReturnsTooLarge_WithoutReading()
    {
        var settings = EnabledSettings();
        settings["thumb_max_source_mb"] = "1";
        var (service, _) = await SetupAsync(settings);

        var result = await service.GetAsync(new ThumbnailRequest("/test/big.jpg"), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(ThumbnailStatus.Failed);
        await Assert.That(result.Reason).IsEqualTo("too-large");
    }

    [Test]
    public async Task GetAsync_WithoutFfmpeg_ReturnsFfmpegUnavailable()
    {
        // 指向一个必然不存在的可执行文件：结果与机器上是否装了 ffmpeg 无关。
        var settings = EnabledSettings();
        settings["thumb_ffmpeg_path"] = Path.Combine(Path.GetTempPath(), "definitely-no-ffmpeg", "ffmpeg");
        var (service, _) = await SetupAsync(settings);

        var result = await service.GetAsync(new ThumbnailRequest("/test/a.jpg"), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(ThumbnailStatus.Failed);
        await Assert.That(result.Reason).IsEqualTo("ffmpeg-unavailable");
    }

    [Test]
    public async Task GetAsync_VideoWithoutSeekableSource_ReturnsNoSeekableSource()
    {
        // 内存文件系统既不是本地物理路径，也不提供直链 —— 必须明确拒绝，而不是把顺序流硬塞给 ffmpeg。
        var (service, _) = await SetupAsync(EnabledSettings());
        var result = await service.GetAsync(new ThumbnailRequest("/test/a.mp4"), CancellationToken.None);

        // ffmpeg 缺失的机器上会先在解析阶段失败；装有 ffmpeg 时才会走到"没有可随机读的源"。
        await Assert.That(result.Status).IsEqualTo(ThumbnailStatus.Failed);
        await Assert.That(result.Reason is "no-seekable-source" or "ffmpeg-unavailable").IsTrue();
    }

    // ---- 装配 ----

    private static ThumbnailOptions EnabledOptions() => new()
    {
        Enabled = true,
        MaxSize = 320,
        Quality = 80,
        CacheFolder = Path.Combine(Path.GetTempPath(), "openlist-thumb-tests"),
    };

    private static Dictionary<string, string> EnabledSettings() => new()
    {
        ["thumb_enable"] = "true",
        ["thumb_max_size"] = "320",
        ["thumb_max_source_mb"] = "64",
        ["thumb_cache_folder"] = Path.Combine(Path.GetTempPath(), "openlist-thumb-tests"),
    };

    private static async Task<(ThumbnailService Service, MemoryFileSystem Mem)> SetupAsync(Dictionary<string, string> settings)
    {
        var mount = new MountService();
        var mem = new MemoryFileSystem();
        mount.Root.Mount("/test", mem);

        WriteFile(mem, "/a.jpg", [0xFF, 0xD8, 0xFF, 0xE0]);
        WriteFile(mem, "/a.mp4", [0x00, 0x00, 0x00, 0x18]);
        WriteFile(mem, "/notes.txt", [0x68, 0x69]);
        // 2MB：超过测试里设的 1MB 上限，用于验证"不读内容就拒绝"。
        WriteFile(mem, "/big.jpg", new byte[2 * 1024 * 1024]);

        var config = new AppConfiguration { Database = new DatabaseConfig { DBFile = Path.Combine(Path.GetTempPath(), "openlist-thumb-tests", "data.db") } };
        var services = new ServiceCollection();
        services.AddSingleton<ISettingService>(new FakeSettings(settings));
        var provider = services.BuildServiceProvider();
        var options = new ThumbnailOptionsProvider(
            provider.GetRequiredService<IServiceScopeFactory>(),
            config,
            NullLogger<ThumbnailOptionsProvider>.Instance);
        await options.RefreshAsync();

        var runner = new FfmpegProcessRunner(NullLogger<FfmpegProcessRunner>.Instance);
        var service = new ThumbnailService(
            mount,
            options,
            new ThumbnailCache(),
            new FfmpegTool(runner, NullLogger<FfmpegTool>.Instance),
            runner,
            NullLogger<ThumbnailService>.Instance);
        return (service, mem);
    }

    private static void WriteFile(MemoryFileSystem mem, string path, byte[] bytes)
    {
        using var s = mem.OpenFile(new UPath(path), FileMode.Create, FileAccess.Write);
        s.Write(bytes, 0, bytes.Length);
    }

    /// <summary>只实现 <see cref="ISettingService.ListAsync"/> 的桩，其余成员不应被缩略图链路调用。</summary>
    private sealed class FakeSettings(Dictionary<string, string> values) : ISettingService
    {
        public Task<Resp<List<SettingItem>>> ListAsync(CancellationToken ct = default)
            => Task.FromResult(Resp<List<SettingItem>>.Ok(
                [.. values.Select(kv => new SettingItem { Key = kv.Key, Value = kv.Value })]));

        public Task<Resp<SettingItem>> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(Resp<SettingItem>.Fail("not found", RespCodes.NotFound));

        public Task<Resp> SaveAsync(List<SettingItem> items, CancellationToken ct = default)
            => Task.FromResult(Resp.Ok());

        public Task<Resp> DeleteAsync(string key, CancellationToken ct = default)
            => Task.FromResult(Resp.Ok());

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

    /// <summary>固定签名的签名服务替身。</summary>
    private sealed class FakeSignService : ISignService
    {
        public string Sign(string path, long expireAt) => "fake-sign";

        public bool Verify(string path, string sign) => sign == "fake-sign";

        public Task<SignContext> GetContextAsync(string path, CancellationToken ct = default)
            => Task.FromResult(new SignContext(true, 0));
    }
}
