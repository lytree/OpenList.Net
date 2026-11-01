using PDFtoImage;
using PDFtoImage.Exceptions;

namespace OpenList.Infrastructure.Thumbnails;

/// <summary>
/// PDF 首页 → PNG（PDFium，经 <c>PDFtoImage</c> 调用）。
/// <para>
/// <b>为什么这里破例引入了进程内 native</b>：缩略图其余类型全部交给外部 ffmpeg 进程
/// （见 <see cref="ThumbnailService"/> 的说明），但 ffmpeg 不解 PDF，而 PDF 又只有 PDFium 这一个
/// 实用选项。换句话说这不是「顺手再引一套 native」，是「PDF 缩略图」这个需求本身的最低成本路径。
/// 代价与缓解：
/// </para>
/// <list type="bullet">
/// <item>体积：PDFium 每个 RID 约 10MB（SkiaSharp 桌面端本来就有，Avalonia 依赖它，增量只有 PDFium）。</item>
/// <item>稳定性：PDFium 解析畸形文件理论上可能崩掉进程。缓解是
/// <see cref="LibraryTtl"/> 之外的<strong>全局串行化</strong>（PDFium 不是线程安全的）+
/// 上层 15s 超时 + 源文件大小上限（<c>thumb_max_source_mb</c>）。真要彻底隔离只能开子进程，
/// 那等于为一种文件类型再维护一个可执行体，收益不抵成本。</item>
/// <item>Linux：<c>PDFtoImage</c> 拉的是 <c>SkiaSharp.NativeAssets.Linux.NoDependencies</c>，
/// 自带 fontconfig，镜像不需要额外装包。</item>
/// </list>
/// <para>
/// <b>只渲染首页</b>，且不尝试密码：加密 PDF 直接归入 <c>pdf-encrypted</c> 负缓存，
/// 反复猜密码既不礼貌也会把 CPU 打满。
/// </para>
/// </summary>
internal static class PdfRenderer
{
    /// <summary>栅格化 DPI。目标边长最多 1024px，120 DPI 足够在缩小时保住细节，又不至于把内存放大。</summary>
    private const int RenderDpi = 120;

    /// <summary>PDFium 不是线程安全的，渲染全局串行。缩略图有缓存，串行的实际代价很低。</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>渲染首页到 <paramref name="pngPath"/>；失败返回可枚举原因（不含路径）。</summary>
    /// <remarks>
    /// 同步 API 包在异步签名里是有意的：渲染是纯 CPU 活，阻塞一个线程池线程是诚实的；
    /// 但<strong>等锁必须异步</strong>，否则并发请求会瞬间占满线程池（线程饥饿）。
    /// </remarks>
    public static async Task<(bool Ok, string? Reason)> RenderFirstPageAsync(
        string pdfPath, string pngPath, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var pdf = new FileStream(pdfPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
            using var png = new FileStream(pngPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
            Conversion.SavePng(png, pdf, 0, leaveOpen: false, password: null, new RenderOptions { Dpi = RenderDpi });
            return (true, null);
        }
        catch (PdfPasswordProtectedException) { return (false, "pdf-encrypted"); }
        catch (PdfUnsupportedSecuritySchemeException) { return (false, "pdf-encrypted"); }
        catch (PdfInvalidFormatException) { return (false, "pdf-invalid"); }
        catch (PdfCannotOpenFileException) { return (false, "pdf-invalid"); }
        catch (PdfPageNotFoundException) { return (false, "pdf-invalid"); }
        catch (PdfException) { return (false, "decode-failed"); }
        // native 加载不起来（Linux 缺 libstdc++、或发布产物没带 libpdfium）。
        // 与 ffmpeg 缺失同一口径：这类失败要能一眼从 X-Thumb-Reason 上看出来。
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or BadImageFormatException)
        {
            return (false, "pdfium-unavailable");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return (false, "decode-failed"); }
        finally
        {
            Gate.Release();
        }
    }
}
