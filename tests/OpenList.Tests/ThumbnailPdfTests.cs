using System.Buffers.Binary;
using System.Globalization;
using System.Text;
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
/// PDF 首页缩略图。
/// <para>
/// 分两层验证：<see cref="PdfRenderer"/> 直接测（不需要 ffmpeg，能独立证明 PDFium 这套 native
/// 在当前 RID 上真的能加载并出图）；<see cref="ThumbnailService"/> 再测一次全链路
/// （PDFium 出 PNG → ffmpeg 缩放编码），这条需要 ffmpeg，缺失时直接返回。
/// </para>
/// </summary>
public class ThumbnailPdfTests
{
    /// <summary>
    /// 手拼一个最小但合法的单页 PDF（200×200pt，内容是一行 Helvetica 文字）。
    /// 依赖解析器的宽容度是最容易出问题的地方，所以这里把 xref 偏移算准，不靠"修复"。
    /// </summary>
    private static byte[] BuildPdf(string label = "OpenList")
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Push(string text)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(sb.ToString()));
            sb.Append(text);
        }

        sb.Append("%PDF-1.4\n");
        Push("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Push("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Push("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
             "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        var stream = $"BT /F1 20 Tf 30 100 Td ({label}) Tj ET";
        Push(string.Create(CultureInfo.InvariantCulture,
            $"4 0 obj\n<< /Length {stream.Length} >>\nstream\n{stream}\nendstream\nendobj\n"));
        Push("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        var xref = Encoding.Latin1.GetByteCount(sb.ToString());
        var tail = new StringBuilder("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            tail.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        tail.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        sb.Append(tail);

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    // ---- PdfRenderer（不依赖 ffmpeg）----

    [Test]
    public async Task RenderFirstPage_ProducesPng()
    {
        var dir = NewTempDir();
        var pdfPath = Path.Combine(dir, "doc.pdf");
        var pngPath = Path.Combine(dir, "page.png");
        await File.WriteAllBytesAsync(pdfPath, BuildPdf());

        var (ok, reason) = await PdfRenderer.RenderFirstPageAsync(pdfPath, pngPath, CancellationToken.None);

        await Assert.That(ok).IsTrue();
        await Assert.That(reason).IsNull();
        await Assert.That(File.Exists(pngPath)).IsTrue();

        var png = await File.ReadAllBytesAsync(pngPath);
        // PNG 魔数：如果不是 PNG，这里就会挂，比只看文件大小可靠。
        await Assert.That(png.Length).IsGreaterThan(100);
        await Assert.That(png[0]).IsEqualTo((byte)0x89);
        await Assert.That(png[1]).IsEqualTo((byte)'P');
        await Assert.That(png[2]).IsEqualTo((byte)'N');
        await Assert.That(png[3]).IsEqualTo((byte)'G');

        // 200pt @120dpi ≈ 333px（PDFium 按 72dpi 为基准）。只校验数量级，避免和实现细节绑死。
        var (width, height) = PngSize(png);
        await Assert.That(width).IsBetween(300, 360);
        await Assert.That(height).IsBetween(300, 360);
    }

    [Test]
    public async Task RenderFirstPage_InvalidPdf_ReturnsReason()
    {
        var dir = NewTempDir();
        var pdfPath = Path.Combine(dir, "broken.pdf");
        var pngPath = Path.Combine(dir, "page.png");
        // 「看着像 PDF 但打不开」：真实场景里就是下载中断的半截文件。
        await File.WriteAllTextAsync(pdfPath, "%PDF-1.4\nthis is not a pdf body\n", Encoding.Latin1);

        var (ok, reason) = await PdfRenderer.RenderFirstPageAsync(pdfPath, pngPath, CancellationToken.None);

        await Assert.That(ok).IsFalse();
        await Assert.That(reason).IsIn("pdf-invalid", "decode-failed");
        // 失败时不能留下半张图（下次命中缓存就成了裂图）。
        await Assert.That(File.Exists(pngPath) && new FileInfo(pngPath).Length > 0).IsFalse();
    }

    // ---- 全链路 ----

    [Test]
    public async Task Pdf_ThroughService_ProducesJpegAtRequestedEdge()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;

        var (service, mount) = Setup(ffmpeg);
        Write(mount, "/test/doc.pdf", BuildPdf());

        var result = await service.GetAsync(new ThumbnailRequest("/test/doc.pdf", 200, 200), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(ThumbnailStatus.Ok);
        await Assert.That(result.ContentType).IsEqualTo("image/jpeg");
        // JPEG 的 SOI，确认拿到的是图而不是一段 HTML 错误页。
        await Assert.That(result.Data![0]).IsEqualTo((byte)0xFF);
        await Assert.That(result.Data[1]).IsEqualTo((byte)0xD8);
    }

    [Test]
    public async Task Pdf_BrokenThroughService_FailsWithPdfReason()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null) return;

        var (service, mount) = Setup(ffmpeg);
        Write(mount, "/test/broken.pdf", Encoding.Latin1.GetBytes("%PDF-1.4\ngarbage\n"));

        var result = await service.GetAsync(new ThumbnailRequest("/test/broken.pdf"), CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(ThumbnailStatus.Failed);
        await Assert.That(result.Reason).IsIn("pdf-invalid", "decode-failed");
    }

    [Test]
    public async Task Probe_AllowsPdf()
    {
        var (service, mount) = Setup(ffmpeg: null);
        Write(mount, "/test/doc.pdf", BuildPdf());

        var probe = service.Probe(new ThumbnailRequest("/test/doc.pdf"));

        // HEAD /p?type=thumb 对 PDF 必须回 200：列表里已经下发 thumb 了，
        // 探测说「不支持」会让前端把封面藏起来。
        await Assert.That(probe.Status).IsEqualTo(ThumbnailStatus.Ok);
    }

    // ---- 装配 ----

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openlist-thumb-pdf", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (int Width, int Height) PngSize(byte[] png)
    {
        // IHDR 紧跟 8 字节签名：长度(4) + "IHDR"(4) + width(4) + height(4)
        var width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        return (width, height);
    }

    private static (ThumbnailService Service, MountService Mount) Setup(string? ffmpeg)
    {
        var mount = new MountService();
        mount.Root.Mount("/test", new MemoryFileSystem());

        var settings = new Dictionary<string, string>
        {
            ["thumb_enable"] = "true",
            ["thumb_max_size"] = "320",
            ["thumb_max_source_mb"] = "64",
            ["thumb_types"] = "jpg,png,pdf",
            ["thumb_cache_folder"] = Path.Combine(Path.GetTempPath(), "openlist-thumb-pdf", "cache", Guid.NewGuid().ToString("N")),
        };
        if (ffmpeg is not null) settings["thumb_ffmpeg_path"] = ffmpeg;

        var config = new AppConfiguration
        {
            Database = new DatabaseConfig { DBFile = Path.Combine(Path.GetTempPath(), "openlist-thumb-pdf", "data.db") },
        };
        var services = new ServiceCollection();
        services.AddSingleton<ISettingService>(new FakeSettings(settings));
        var provider = services.BuildServiceProvider();
        var options = new ThumbnailOptionsProvider(
            provider.GetRequiredService<IServiceScopeFactory>(), config, NullLogger<ThumbnailOptionsProvider>.Instance);
        options.RefreshAsync().GetAwaiter().GetResult();

        var runner = new FfmpegProcessRunner(NullLogger<FfmpegProcessRunner>.Instance);
        var service = new ThumbnailService(
            mount, options, new ThumbnailCache(), new FfmpegTool(runner, NullLogger<FfmpegTool>.Instance),
            runner, NullLogger<ThumbnailService>.Instance);
        return (service, mount);
    }

    private static void Write(MountService mount, string path, byte[] bytes)
    {
        using var s = mount.Root.OpenFile(new UPath(path), FileMode.Create, FileAccess.Write);
        s.Write(bytes, 0, bytes.Length);
    }

    /// <summary>只提供 <see cref="ISettingService.ListAsync"/> 的假实现（快照刷新只走这一条）。</summary>

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

        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var candidate = Path.Combine(p.Trim('"'), "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
