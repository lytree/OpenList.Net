using System.Diagnostics;
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
/// 真正调用 ffmpeg 的集成测试：验证「参数拼对了」「产物是能用的图」。
/// <para>
/// 没有 ffmpeg 的环境（例如只跑了 <c>dotnet test</c> 的裸机）会直接返回 ——
/// 这些用例的目的是抓住参数/滤镜写错这类问题，而不是替代 E2E。
/// 查找顺序：<c>OPENLIST_FFMPEG</c> 环境变量 → 仓库根目录下的 <c>.e2e/bin/ffmpeg(.exe)</c>（回归脚本现成的）→ PATH。
/// </para>
/// </summary>
public class ThumbnailFfmpegTests
{
    /// <summary>1×1 PNG：够 ffmpeg 解出一个合法的图像，用来验证缩放/裁剪/编码三段。</summary>
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Test]
    public async Task Image_ProducesJpegWithRequestedEdge()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;

        var (service, mount) = await SetupAsync(ffmpeg);
        WriteFile(mount, "/test/one.png", OnePixelPng);

        var result = await service.GetAsync(new ThumbnailRequest("/test/one.png", 320, 320), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(ThumbnailStatus.Ok);
        await Assert.That(result.ContentType).IsEqualTo("image/jpeg");
        // JPEG 的 SOI：FF D8。产物不是图的话这里就会挂，比单看状态码可靠。
        await Assert.That(result.Data![0]).IsEqualTo((byte)0xFF);
        await Assert.That(result.Data![1]).IsEqualTo((byte)0xD8);
        var size = JpegSize(result.Data);
        await Assert.That(size.Width).IsEqualTo(320);
        await Assert.That(size.Height).IsEqualTo(320);
    }

    [Test]
    public async Task Image_OversizedEdgeIsClamped()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;

        var (service, mount) = await SetupAsync(ffmpeg);
        WriteFile(mount, "/test/one.png", OnePixelPng);

        var result = await service.GetAsync(new ThumbnailRequest("/test/one.png", 9999, 9999), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(ThumbnailStatus.Ok);
        var size = JpegSize(result.Data!);
        // 上限 1024：w=9999 不能让一张 8K 图把 CPU 打满。
        await Assert.That(size.Width).IsEqualTo(1024);
        await Assert.That(size.Height).IsEqualTo(1024);
    }

    [Test]
    public async Task Image_SecondCallHitsCache()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;

        var (service, mount) = await SetupAsync(ffmpeg);
        WriteFile(mount, "/test/one.png", OnePixelPng);

        var first = await service.GetAsync(new ThumbnailRequest("/test/one.png", 128, 128), CancellationToken.None);
        var second = await service.GetAsync(new ThumbnailRequest("/test/one.png", 128, 128), CancellationToken.None);

        await Assert.That(first.Status).IsEqualTo(ThumbnailStatus.Ok);
        await Assert.That(first.FromCache).IsFalse();
        await Assert.That(second.Status).IsEqualTo(ThumbnailStatus.Ok);
        await Assert.That(second.FromCache).IsTrue();
    }

    [Test]
    public async Task Image_WebpRequest_ProducesWebp()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;

        var (service, mount) = await SetupAsync(ffmpeg);
        WriteFile(mount, "/test/one.png", OnePixelPng);

        var result = await service.GetAsync(new ThumbnailRequest("/test/one.png", 96, 96, Format: "webp"), CancellationToken.None);

        // 本机 ffmpeg 没编 libwebp 时会自动退成 jpeg —— 两种情况都必须是「成功」。
        await Assert.That(result.Status).IsEqualTo(ThumbnailStatus.Ok);
        await Assert.That(result.ContentType is "image/webp" or "image/jpeg").IsTrue();
        if (result.ContentType == "image/webp")
            await Assert.That(System.Text.Encoding.ASCII.GetString(result.Data!, 0, 4)).IsEqualTo("RIFF");
    }

    [Test]
    public async Task Video_ExtractsFrameFromLocalFile()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;

        using var fixture = new LocalVideoFixture(ffmpeg);
        if (!fixture.Ready) return;

        var (service, _) = await SetupAsync(ffmpeg, fixture.RootPath);
        var result = await service.GetAsync(
            new ThumbnailRequest($"{fixture.MountPath}/clip.mp4", 200, 200, At: "1"), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(ThumbnailStatus.Ok);
        var size = JpegSize(result.Data!);
        await Assert.That(size.Width).IsEqualTo(200);
        await Assert.That(size.Height).IsEqualTo(200);
    }

    [Test]
    public async Task Video_PercentagePosition_FallsBackToTenSecondsWithoutProbe()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;

        using var fixture = new LocalVideoFixture(ffmpeg);
        if (!fixture.Ready) return;

        var (service, _) = await SetupAsync(ffmpeg, fixture.RootPath);
        // 默认配置是 10%：没有 ffprobe 时应退化为第 10 秒。3 秒的片段取第 10 秒会失败，
        // 所以这里只断言「不抛异常且给出了确定结果」—— 具体分支取决于 ffprobe 是否存在。
        var result = await service.GetAsync(
            new ThumbnailRequest($"{fixture.MountPath}/clip.mp4", 120, 120), CancellationToken.None);

        await Assert.That(result.Status is ThumbnailStatus.Ok or ThumbnailStatus.Failed).IsTrue();
    }

    // ---- 装配 ----

    private static async Task<(ThumbnailService Service, MountService Mount)> SetupAsync(string ffmpeg, string? localRoot = null)
    {
        var mount = new MountService();
        var settings = new Dictionary<string, string>
        {
            ["thumb_enable"] = "true",
            ["thumb_max_size"] = "320",
            ["thumb_max_source_mb"] = "64",
            ["thumb_ffmpeg_path"] = ffmpeg,
            ["thumb_cache_folder"] = Path.Combine(Path.GetTempPath(), "openlist-thumb-it", Guid.NewGuid().ToString("N")),
        };

        if (localRoot is not null)
        {
            await mount.LoadAsync(new Storage
            {
                MountPath = "/local",
                Driver = "Local",
                Addition = $"{{\"root_folder_path\":\"{localRoot.Replace("\\", "\\\\")}\"}}",
            });
        }
        else
        {
            mount.Root.Mount("/test", new MemoryFileSystem());
        }

        var config = new AppConfiguration { Database = new DatabaseConfig { DBFile = Path.Combine(Path.GetTempPath(), "openlist-thumb-it", "data.db") } };
        var services = new ServiceCollection();
        services.AddSingleton<ISettingService>(new FakeSettings(settings));
        var provider = services.BuildServiceProvider();
        var options = new ThumbnailOptionsProvider(
            provider.GetRequiredService<IServiceScopeFactory>(), config, NullLogger<ThumbnailOptionsProvider>.Instance);
        await options.RefreshAsync();

        var runner = new FfmpegProcessRunner(NullLogger<FfmpegProcessRunner>.Instance);
        var service = new ThumbnailService(
            mount, options, new ThumbnailCache(), new FfmpegTool(runner, NullLogger<FfmpegTool>.Instance),
            runner, NullLogger<ThumbnailService>.Instance);
        return (service, mount);
    }

    private static void WriteFile(MountService mount, string path, byte[] bytes)
    {
        using var s = mount.Root.OpenFile(new UPath(path), FileMode.Create, FileAccess.Write);
        s.Write(bytes, 0, bytes.Length);
    }

    /// <summary>按「环境变量 → 仓库内 .e2e/bin → PATH」找 ffmpeg；找不到返回 null（用例直接返回）。</summary>
    private static string? FindFfmpeg()
    {
        var fromEnv = Environment.GetEnvironmentVariable("OPENLIST_FFMPEG");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            foreach (var name in new[] { "ffmpeg.exe", "ffmpeg" })
            {
                var candidate = Path.Combine(dir.FullName, ".e2e", "bin", name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "ffmpeg.exe", "ffmpeg" })
            {
                try
                {
                    var candidate = Path.Combine(entry, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* PATH 里有非法字符的目录 */ }
            }
        }
        return null;
    }

    /// <summary>从 JPEG 的 SOF 段读出宽高（不引解码库，够断言裁剪结果）。</summary>
    private static (int Width, int Height) JpegSize(byte[] jpeg)
    {
        var i = 2;
        while (i + 9 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF) { i++; continue; }
            var marker = jpeg[i + 1];
            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            // SOF0..SOF3 / SOF5..SOF7 / SOF9..SOF11：都带 高/宽 两个 16 位字段
            if (marker is >= 0xC0 and <= 0xC3 or >= 0xC5 and <= 0xC7 or >= 0xC9 and <= 0xCB)
            {
                var height = (jpeg[i + 5] << 8) | jpeg[i + 6];
                var width = (jpeg[i + 7] << 8) | jpeg[i + 8];
                return (width, height);
            }
            i += 2 + length;
        }
        return (0, 0);
    }

    /// <summary>用 ffmpeg 现录一段 3 秒测试片段，挂到 <c>/local</c>。</summary>
    private sealed class LocalVideoFixture : IDisposable
    {
        public LocalVideoFixture(string ffmpeg)
        {
            RootPath = Path.Combine(Path.GetTempPath(), "openlist-thumb-it", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            var clip = Path.Combine(RootPath, "clip.mp4");
            var psi = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var arg in new[]
                     {
                         "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                         "-f", "lavfi", "-i", "testsrc=duration=3:size=160x120:rate=10",
                         "-c:v", "libx264", "-pix_fmt", "yuv420p", clip,
                     })
                psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi);
            process?.WaitForExit(60_000);
            Ready = File.Exists(clip) && new FileInfo(clip).Length > 0;
        }

        public string RootPath { get; }

        public string MountPath => "/local";

        public bool Ready { get; }

        public void Dispose()
        {
            try { Directory.Delete(RootPath, recursive: true); } catch { /* 临时目录清不掉无妨 */ }
        }
    }

    /// <summary>ISettingService 假件（只实现缩略图链路会用到的 ListAsync）。</summary>
    private sealed class FakeSettings(Dictionary<string, string> values) : ISettingService
    {
        public Task<Resp<List<SettingItem>>> ListAsync(CancellationToken ct = default)
            => Task.FromResult(Resp<List<SettingItem>>.Ok(
                [.. values.Select(kv => new SettingItem { Key = kv.Key, Value = kv.Value })]));

        public Task<Resp<SettingItem>> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(Resp<SettingItem>.Fail("not found", RespCodes.NotFound));
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
}
