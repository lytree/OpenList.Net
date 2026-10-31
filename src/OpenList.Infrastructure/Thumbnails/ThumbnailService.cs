using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenList.Core.Drivers;
using OpenList.Infrastructure.Services;
using OpenList.Vfs;

namespace OpenList.Infrastructure.Thumbnails;

/// <summary>缩略图请求的结果状态，端点据此映射 HTTP 状态码。</summary>
public enum ThumbnailStatus
{
    /// <summary>拿到图片。</summary>
    Ok = 0,

    /// <summary>文件不存在 / 无权（对外一律 404，与 <c>MediaImages</c> 同口径）。</summary>
    NotFound = 1,

    /// <summary>类型不支持（文本、压缩包、SVG、PDF…）→ 415。</summary>
    Unsupported = 2,

    /// <summary>生成失败（解码错、源不可随机读、超限、ffmpeg 缺失）→ 404 + <c>X-Thumb-Reason</c>。</summary>
    Failed = 3,

    /// <summary>并发饱和 → 503 + <c>Retry-After</c>。</summary>
    Busy = 4,
}

/// <summary>规范化后的缩略图请求。</summary>
/// <param name="Path">文件的完整挂载路径。</param>
/// <param name="Width">目标宽度（像素，可空 = 用设置项）。</param>
/// <param name="Height">目标高度（像素，可空 = 用设置项）。</param>
/// <param name="At">视频抽帧位置（秒或百分比，可空 = 用设置项）。</param>
/// <param name="Format">编码格式：<c>auto|webp|jpeg|png</c>。</param>
/// <param name="Quality">编码质量 1–100。</param>
public sealed record ThumbnailRequest(
    string Path,
    int? Width = null,
    int? Height = null,
    string? At = null,
    string? Format = null,
    int? Quality = null);

/// <summary>缩略图结果。失败时 <see cref="Data"/> 为空，<see cref="Reason"/> 是可枚举的诊断值（不含路径）。</summary>
/// <param name="Status">状态。</param>
/// <param name="Reason">失败原因（诊断用，可空）。</param>
/// <param name="Data">图片字节。</param>
/// <param name="ContentType">响应的 Content-Type。</param>
/// <param name="FromCache">是否来自缓存（只用于 <c>X-Thumb-Cache</c> 头，便于 E2E 断言）。</param>
public sealed record ThumbnailResult(
    ThumbnailStatus Status,
    string? Reason,
    byte[]? Data,
    string ContentType,
    bool FromCache)
{
    /// <summary>命中或生成成功。</summary>
    public static ThumbnailResult Ok(byte[] data, string contentType, bool fromCache)
        => new(ThumbnailStatus.Ok, null, data, contentType, fromCache);

    /// <summary>类型不支持。</summary>
    public static ThumbnailResult Unsupported(string reason)
        => new(ThumbnailStatus.Unsupported, reason, null, "application/octet-stream", false);

    /// <summary>文件不存在。</summary>
    public static ThumbnailResult NotFound(string reason)
        => new(ThumbnailStatus.NotFound, reason, null, "application/octet-stream", false);

    /// <summary>生成失败。</summary>
    public static ThumbnailResult Failed(string reason)
        => new(ThumbnailStatus.Failed, reason, null, "application/octet-stream", false);

    /// <summary>并发饱和。</summary>
    public static ThumbnailResult Busy()
        => new(ThumbnailStatus.Busy, "busy", null, "application/octet-stream", false);
}

/// <summary>
/// 缩略图服务：判定类型 → 查缓存 → 调 ffmpeg 生成 → 回填缓存。
/// <para>
/// <b>生成全部交给外部 ffmpeg 进程</b>（与 Jellyfin 同一路线，见
/// <c>docs/thumbnail-service-plan.md</c> §5 修订记录）。这样做的取舍：
/// </para>
/// <list type="bullet">
/// <item>✅ 进程内不链接任何 native 库：native 崩溃只死子进程，不会带走整个服务；
/// 也不需要为 6 个 RID 各打包一份解码器（桌面版体积不涨）。</item>
/// <item>✅ 许可证干净：调用系统/镜像里的 ffmpeg 是「独立程序」而非动态链接，
/// 按 MIT 发布时不必附带 LGPL 的「可替换库」说明（仍建议在 NOTICE 致谢）。</item>
/// <item>✅ 参数可复制出来手跑，排障成本低；能力按 <c>-version/-encoders</c> 探测一次。</item>
/// <item>❌ 需要部署环境有 ffmpeg；缺失时该类型直接降级为 404 <c>ffmpeg-unavailable</c>，
/// 列表页仍然工作（只是不出图）。Docker 镜像里已预装，桌面版走设置项/环境变量指定。</item>
/// </list>
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    /// <summary>负缓存（失败）默认有效期。</summary>
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(5);

    /// <summary>解码类失败的有效期更长：这类失败是文件级的确定失败，重试无意义。</summary>
    private static readonly TimeSpan DecodeFailureTtl = TimeSpan.FromMinutes(30);

    /// <summary>等全局并发许可的最长时间，超时即 503（宁可不给图，也别把线程池占满）。</summary>
    private static readonly TimeSpan GateWait = TimeSpan.FromSeconds(3);

    /// <summary>从 ffmpeg 的信息输出里抓 <c>Duration: HH:MM:SS.ss</c>。</summary>
    private static readonly Regex DurationRegex = new(
        @"Duration:\s*(\d{2}):(\d{2}):(\d{2}(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly MountService _mount;
    private readonly ThumbnailOptionsProvider _options;
    private readonly ThumbnailCache _cache;
    private readonly FfmpegTool _ffmpeg;
    private readonly FfmpegProcessRunner _runner;
    private readonly ILogger<ThumbnailService> _log;

    private readonly ConcurrentDictionary<string, Lazy<Task<ThumbnailResult>>> _inflight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTimeOffset ExpireAt, string Reason)> _negative = new(StringComparer.Ordinal);

    private readonly object _gateLock = new();
    private SemaphoreSlim _gate = new(Math.Max(2, Environment.ProcessorCount));
    private int _gateSize = Math.Max(2, Environment.ProcessorCount);

    /// <summary>创建服务。</summary>
    public ThumbnailService(
        MountService mount,
        ThumbnailOptionsProvider options,
        ThumbnailCache cache,
        FfmpegTool ffmpeg,
        FfmpegProcessRunner runner,
        ILogger<ThumbnailService> log)
    {
        _mount = mount;
        _options = options;
        _cache = cache;
        _ffmpeg = ffmpeg;
        _runner = runner;
        _log = log;
    }

    /// <summary>
    /// 取缩略图：命中缓存直接返回，否则生成并落盘。
    /// </summary>
    public async Task<ThumbnailResult> GetAsync(ThumbnailRequest request, CancellationToken ct)
    {
        var options = _options.Current;
        if (!options.Enabled) return ThumbnailResult.Unsupported("disabled");

        var kind = ThumbKinds.FromName(request.Path);
        if (kind == ThumbKind.None) return ThumbnailResult.Unsupported("unsupported");
        // PDF 首页需要 PDFium；当前刻意返回 415 而不是偷偷让 ffmpeg 去猜（它猜不出来）。
        if (kind == ThumbKind.Pdf) return ThumbnailResult.Unsupported("pdf-unsupported");
        if (kind == ThumbKind.Video && !options.VideoEnabled) return ThumbnailResult.Unsupported("video-disabled");

        var upath = new UPath(Normalize(request.Path));
        if (!_mount.Root.FileExists(upath)) return ThumbnailResult.NotFound("not-found");
        var entry = _mount.Root.GetFileEntry(upath);

        var extension = ExtensionOf(entry.Name);
        if (!options.AllowsExtension(extension)) return ThumbnailResult.Unsupported("unsupported");
        if (kind != ThumbKind.Video && entry.Length > options.MaxSourceBytes) return ThumbnailResult.Failed("too-large");

        var width = Clamp(request.Width ?? options.MaxSize, 16, 1024);
        var height = Clamp(request.Height ?? options.MaxSize, 16, 1024);
        var format = NormalizeFormat(request.Format);
        var quality = Clamp(request.Quality ?? options.Quality, 1, 100);
        var at = kind == ThumbKind.Video ? (request.At ?? options.VideoPosition).Trim() : string.Empty;

        var key = BuildKey(entry.Path.ToString(), entry.Length, entry.LastWriteTime.ToUniversalTime().Ticks, width, height, format, quality, at);

        if (_negative.TryGetValue(key, out var negative))
        {
            if (negative.ExpireAt > DateTimeOffset.UtcNow) return ThumbnailResult.Failed(negative.Reason);
            _negative.TryRemove(key, out _);
        }

        var cached = _cache.TryGetMemory(key);
        if (cached is not null) return ThumbnailResult.Ok(cached, ContentTypeOfBytes(cached, format), true);

        var fromDisk = await ReadCacheAsync(options, key, format, ct).ConfigureAwait(false);
        if (fromDisk is not null)
        {
            _cache.PutMemory(key, fromDisk);
            return ThumbnailResult.Ok(fromDisk, ContentTypeOfBytes(fromDisk, format), true);
        }

        // 单飞：同一 key 的并发请求只生成一份。没有这一条，一屏 40 张图会触发 40 次相同解码
        // （浏览器对同一 URL 会去重，但不同 srcset 档位不会）。
        var lazy = _inflight.GetOrAdd(
            key,
            _ => new Lazy<Task<ThumbnailResult>>(
                () => GenerateAsync(key, options, entry, kind, width, height, format, quality, at),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            // 只移除自己这一份，避免把后来者的 Lazy 顶掉。
            if (lazy.IsValueCreated && lazy.Value.IsCompleted)
                _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<ThumbnailResult>>>(key, lazy));
        }
    }

    /// <summary>
    /// 只探测、不生成：给 <c>HEAD /p?type=thumb</c> 用。
    /// </summary>
    /// <remarks>
    /// 与 <c>StreamViaProxyAsync</c> 对 HEAD 的约定一致 —— 探测不应消耗解码 CPU 与远端流量。
    /// 因此这里只回「类型是否可生成 + 文件是否存在」，不保证后续 GET 一定成功。
    /// </remarks>
    public ThumbnailResult Probe(ThumbnailRequest request)
    {
        var options = _options.Current;
        if (!options.Enabled) return ThumbnailResult.Unsupported("disabled");

        var kind = ThumbKinds.FromName(request.Path);
        if (kind is ThumbKind.None or ThumbKind.Pdf) return ThumbnailResult.Unsupported("unsupported");
        if (kind == ThumbKind.Video && !options.VideoEnabled) return ThumbnailResult.Unsupported("video-disabled");

        if (!_mount.Root.FileExists(new UPath(Normalize(request.Path))))
            return ThumbnailResult.NotFound("not-found");

        return new ThumbnailResult(ThumbnailStatus.Ok, null, null, ContentTypeOf(NormalizeFormat(request.Format)), false);
    }

    /// <summary>缓存键：任一维度变化都会得到新键，因此不需要任何主动失效钩子。</summary>
    /// <remarks>
    /// 必须含 <c>size</c> 与 <c>mtime</c>：这是唯一可靠的失效信号（覆盖上传会改 mtime；
    /// 少数盘的 mtime 不可靠，用 size 兜底）。必须含 <c>GeneratorVersion</c>：
    /// 否则改了生成参数后老缓存会一直命中。
    /// </remarks>
    public static string BuildKey(
        string path, long size, long mtimeTicks, int width, int height, string format, int quality, string at)
    {
        var raw = string.Create(CultureInfo.InvariantCulture,
            $"v{ThumbnailOptions.GeneratorVersion}|{path}|{size}|{mtimeTicks}|{width}x{height}|{format}|{quality}|{at}");
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexStringLower(hash);
    }

    // ---- 生成 ----

    private async Task<ThumbnailResult> GenerateAsync(
        string key,
        ThumbnailOptions options,
        FileEntry entry,
        ThumbKind kind,
        int width,
        int height,
        string format,
        int quality,
        string at)
    {
        var gate = EnsureGate(options);
        if (!await gate.WaitAsync(GateWait, CancellationToken.None).ConfigureAwait(false))
        {
            _log.LogWarning("thumb busy: kind={Kind} elapsed=gate-wait", kind);
            return ThumbnailResult.Busy();
        }

        var started = DateTimeOffset.UtcNow;
        try
        {
            using var timeout = new CancellationTokenSource(kind == ThumbKind.Video
                ? TimeSpan.FromSeconds(40)
                : TimeSpan.FromSeconds(15));

            var generated = await GenerateCoreAsync(options, entry, kind, width, height, format, quality, at, timeout.Token)
                .ConfigureAwait(false);

            if (generated.Data is not { Length: > 0 } data)
            {
                var reason = generated.Reason ?? "decode-failed";
                RecordNegative(key, reason);
                _log.LogWarning("thumb failed: path={Path} kind={Kind} reason={Reason} elapsed={Elapsed}ms detail={Detail}",
                    entry.Path, kind, reason, (DateTimeOffset.UtcNow - started).TotalMilliseconds, generated.Detail);
                return ThumbnailResult.Failed(reason);
            }

            await _cache.WriteAsync(options.CacheFolder, key, ExtensionOfFormat(generated.Format), data, options.CacheMaxBytes, CancellationToken.None)
                .ConfigureAwait(false);
            _cache.PutMemory(key, data);
            return ThumbnailResult.Ok(data, ContentTypeOf(generated.Format), false);
        }
        catch (OperationCanceledException)
        {
            RecordNegative(key, "timeout");
            _log.LogWarning("thumb failed: path={Path} kind={Kind} reason=timeout", entry.Path, kind);
            return ThumbnailResult.Failed("timeout");
        }
        catch (Exception ex)
        {
            RecordNegative(key, "decode-failed");
            _log.LogWarning(ex, "thumb failed: path={Path} kind={Kind} reason=exception", entry.Path, kind);
            return ThumbnailResult.Failed("decode-failed");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Generated> GenerateCoreAsync(
        ThumbnailOptions options,
        FileEntry entry,
        ThumbKind kind,
        int width,
        int height,
        string format,
        int quality,
        string at,
        CancellationToken ct)
    {
        var ffmpeg = await _ffmpeg.ResolveAsync(options.FfmpegPath, ct).ConfigureAwait(false);
        if (ffmpeg is null) return new Generated(null, format, "ffmpeg-unavailable", "ffmpeg not found");

        // 请求 webp 但本机 ffmpeg 没编 libwebp：直接按 jpeg 生成，别让用户看到裂图。
        var effectiveFormat = format == "webp" && !ffmpeg.SupportsWebp ? "jpg" : format;

        var tempDir = Path.Combine(Path.GetTempPath(), "openlist-thumb");
        Directory.CreateDirectory(tempDir);
        string? inputFile = null;
        string? outputFile = null;
        try
        {
            string source;
            if (kind == ThumbKind.Video)
            {
                var resolved = await ResolveVideoSourceAsync(entry, ct).ConfigureAwait(false);
                if (resolved is null) return new Generated(null, effectiveFormat, "no-seekable-source", "no local path or direct url");
                source = resolved;
            }
            else
            {
                // 图片 / 音频：整读进临时文件。JPEG 的 EXIF 方向、渐进式解码都需要完整数据，
                // 而 ReadFileAsync 式的 maxBytes 上限已经在外层按设置项挡过一次。
                inputFile = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + "." + ExtensionOf(entry.Name));
                await CopyToFileAsync(entry, inputFile, ct).ConfigureAwait(false);
                source = inputFile;
            }

            outputFile = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + "." + ExtensionOfFormat(effectiveFormat));
            var seek = kind == ThumbKind.Video
                ? await ResolveSeekAsync(ffmpeg, source, at, ct).ConfigureAwait(false)
                : string.Empty;
            var args = BuildArguments(kind, source, outputFile, width, height, effectiveFormat, quality, seek);
            var run = await _runner.RunAsync(ffmpeg.Executable, args, TimeSpan.FromSeconds(kind == ThumbKind.Video ? 35 : 12), ct)
                .ConfigureAwait(false);

            if (!run.Success || !File.Exists(outputFile))
            {
                var reason = ClassifyFailure(kind, run);
                return new Generated(null, effectiveFormat, reason, Shorten(run.StandardError));
            }

            var bytes = await File.ReadAllBytesAsync(outputFile, ct).ConfigureAwait(false);
            if (bytes.Length == 0) return new Generated(null, effectiveFormat, "decode-failed", "empty output");
            return new Generated(bytes, effectiveFormat, null, null);
        }
        finally
        {
            TryDelete(inputFile);
            TryDelete(outputFile);
        }
    }

    /// <summary>
    /// 拼 ffmpeg 参数。
    /// </summary>
    /// <remarks>
    /// 一条硬规则：<c>-vf</c> 的整串（含空格、冒号、逗号）必须作为<b>一个</b> argv 元素传进去，
    /// 由 <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> 负责转义。拼成字符串
    /// 会被子进程按空格拆散，FFmpeg 把碎片当成输出文件名，报错又因为 <c>-loglevel error</c> 看不到原因。
    /// </remarks>
    internal static List<string> BuildArguments(
        ThumbKind kind, string source, string output, int width, int height, string format, int quality, string at)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-loglevel", "error",
            "-y",
        };

        if (kind == ThumbKind.Video && !string.IsNullOrEmpty(at))
        {
            // -ss 放在 -i 之前是「快进」语义：只解关键帧，比解码整段快一到两个数量级。
            args.Add("-ss");
            args.Add(at);
        }

        args.Add("-i");
        args.Add(source);

        if (kind == ThumbKind.Audio)
        {
            // 内嵌封面是「附带图像流」：mp3 的 APIC / flac 的 PICTURE / mp4 的 covr 都映射成 0:v:0。
            args.Add("-map");
            args.Add("0:v:0");
        }

        args.Add("-frames:v");
        args.Add("1");
        args.Add("-an");
        args.Add("-sn");
        args.Add("-vf");
        args.Add(string.Create(CultureInfo.InvariantCulture,
            $"scale={width}:{height}:force_original_aspect_ratio=increase:flags=lanczos,crop={width}:{height}"));

        switch (format)
        {
            case "webp":
                args.Add("-c:v");
                args.Add("libwebp");
                args.Add("-quality");
                args.Add(quality.ToString(CultureInfo.InvariantCulture));
                args.Add("-compression_level");
                args.Add("4");
                break;
            case "png":
                args.Add("-c:v");
                args.Add("png");
                break;
            default:
                args.Add("-c:v");
                args.Add("mjpeg");
                // ffmpeg 的 qscale 是「越小越好」，且有效区间只有 2..31，跟 1..100 正好反向。
                var qscale = Math.Clamp(31 - quality * 29 / 100, 2, 31);
                args.Add("-q:v");
                args.Add(qscale.ToString(CultureInfo.InvariantCulture));
                break;
        }

        args.Add("-f");
        args.Add("image2");
        args.Add(output);
        return args;
    }

    /// <summary>
    /// 把抽帧位置规范成 ffmpeg 能接受的「秒数」。
    /// <para>
    /// 设置项允许 <c>10%</c> 这种写法（对齐 Go 的 <c>video_thumb_pos</c>），但 <c>-ss</c>
    /// 只认秒数，所以百分比必须先用 ffprobe 取时长。<b>ffprobe 缺失时退化为固定 10 秒</b> ——
    /// 抽一帧放在片头附近通常也算合理，比直接失败有用。
    /// </para>
    /// </summary>
    private async Task<string> ResolveSeekAsync(FfmpegInfo ffmpeg, string source, string position, CancellationToken ct)
    {
        var raw = string.IsNullOrWhiteSpace(position) ? "0" : position.Trim();
        if (!raw.EndsWith('%')) return raw;

        if (!double.TryParse(raw.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            return "0";

        var duration = await ProbeDurationAsync(ffmpeg, source, ct).ConfigureAwait(false);
        if (duration is null)
        {
            _log.LogDebug("thumb: ffprobe unavailable, video position '{Position}' falls back to 10s", raw);
            return "10";
        }

        var seconds = Math.Clamp(duration.Value * percent / 100d, 0, Math.Max(0, duration.Value - 0.1));
        return seconds.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>用 ffprobe 取容器时长（秒）；不可用或解析失败返回 null。</summary>
    /// <remarks>
    /// 没装 ffprobe 时退一步用 ffmpeg 自己读头部：执行 <c>ffmpeg -i src</c>（不给输出文件）
    /// 会打印 <c>Duration: 00:01:23.45</c> 然后以退出码 1 结束 —— 只读头部，不解码。
    /// 这条兜底让「百分比抽帧」在只有 ffmpeg 一个二进制时也能工作。
    /// </remarks>
    private async Task<double?> ProbeDurationAsync(FfmpegInfo ffmpeg, string source, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(ffmpeg.ProbeExecutable))
        {
            try
            {
                var run = await _runner.RunAsync(
                        ffmpeg.ProbeExecutable,
                        ["-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", source],
                        TimeSpan.FromSeconds(10),
                        ct)
                    .ConfigureAwait(false);
                if (run.Success && TryParseSeconds(run.StandardOutput.Trim()) is { } seconds)
                    return seconds;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* 退到下面的 ffmpeg 兜底 */ }
        }

        try
        {
            // 刻意不给输出文件：ffmpeg 读完头部就报错退出，不会解码整段。
            var header = await _runner.RunAsync(
                    ffmpeg.Executable,
                    ["-hide_banner", "-nostdin", "-i", source],
                    TimeSpan.FromSeconds(10),
                    ct)
                .ConfigureAwait(false);
            var match = DurationRegex.Match(header.StandardError);
            if (!match.Success) return null;
            var hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            return TryParseSeconds(match.Groups[3].Value) is { } tail
                ? hours * 3600 + minutes * 60 + tail
                : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>解析 "12.345" 这样的秒数；非正数视为无效。</summary>
    private static double? TryParseSeconds(string text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? seconds
            : null;

    /// <summary>把失败归入可枚举的原因，便于 E2E 断言与排障。</summary>
    private static string ClassifyFailure(ThumbKind kind, FfmpegRunResult run)
    {
        if (!run.Started) return "ffmpeg-unavailable";
        if (run.TimedOut) return "timeout";
        var stderr = run.StandardError;
        if (kind == ThumbKind.Audio
            && (stderr.Contains("matches no streams", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("does not contain any stream", StringComparison.OrdinalIgnoreCase)))
            return "no-cover";
        return "decode-failed";
    }

    /// <summary>
    /// 视频的输入源：本地物理路径 > 驱动直链 > 放弃。
    /// <para>
    /// 这里<b>不</b>拿 VFS 的 <c>OpenFile</c> 流去喂 ffmpeg —— 云盘驱动返回的是
    /// <c>CanSeek = false</c> 的顺序流（<c>CallbackRangeReader</c>），而 mp4 的 moov 常在文件尾，
    /// 抽帧必须随机读。给 ffmpeg 一个真实路径或 http URL，它自己会用 pread / HTTP Range 处理。
    /// </para>
    /// </summary>
    private async Task<string?> ResolveVideoSourceAsync(FileEntry entry, CancellationToken ct)
    {
        var fullPath = entry.Path.ToString();
        var driver = _mount.FindDriverForPath(fullPath);

        if (driver is IFileSystemLocalPath local)
        {
            var relative = _mount.ToDriverRelativePath(fullPath);
            if (local.TryGetLocalPath(relative, out var physical) && File.Exists(physical)) return physical;
        }

        if (driver is IFileSystemLinkProvider linkProvider)
        {
            try
            {
                var link = await linkProvider.GetFileLinkAsync(_mount.ToDriverRelativePath(fullPath), proxy: false, ct)
                    .ConfigureAwait(false);
                if (!string.IsNullOrEmpty(link?.Url)) return link.Url;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* 取直链失败就当作没有可用源 */ }
        }

        return null;
    }

    private async Task CopyToFileAsync(FileEntry entry, string target, CancellationToken ct)
    {
        // FileShare.Read：同一张图可能同时被两个不同尺寸的请求读取，独占打开会互相打架。
        using var source = _mount.Root.OpenFile(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var dest = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await source.CopyToAsync(dest, 81920, ct).ConfigureAwait(false);
    }

    private async Task<byte[]?> ReadCacheAsync(ThumbnailOptions options, string key, string format, CancellationToken ct)
    {
        // 命中 webp 但当时 ffmpeg 不支持而落成了 jpg —— 按候选格式依次看落盘结果。
        foreach (var candidate in Candidates(format))
        {
            var data = await _cache.ReadAsync(options.CacheFolder, key, ExtensionOfFormat(candidate), ct).ConfigureAwait(false);
            if (data is not null) return data;
        }
        return null;
    }

    private void RecordNegative(string key, string reason)
    {
        var ttl = reason is "decode-failed" or "no-cover" or "ffmpeg-unavailable" ? DecodeFailureTtl : NegativeTtl;
        _negative[key] = (DateTimeOffset.UtcNow + ttl, reason);

        // 顺手清理过期项：负缓存只在内存里，长期不清理会随着访问过的失败文件缓慢增长。
        if (_negative.Count > 4096)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var pair in _negative)
                if (pair.Value.ExpireAt <= now) _negative.TryRemove(pair.Key, out _);
        }
    }

    private SemaphoreSlim EnsureGate(ThumbnailOptions options)
    {
        var size = options.EffectiveConcurrency;
        if (size == _gateSize) return _gate;
        lock (_gateLock)
        {
            if (size == _gateSize) return _gate;
            _gateSize = size;
            _gate = new SemaphoreSlim(size);
            return _gate;
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 临时文件删不掉不影响请求 */ }
    }

    private static string Shorten(string text)
        => string.IsNullOrEmpty(text) ? string.Empty
            : text.Length <= 400 ? text.Trim()
            : text[^400..].Trim();

    private static string Normalize(string path)
        => string.IsNullOrWhiteSpace(path) ? "/" : path.StartsWith('/') ? path : "/" + path;

    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    /// <summary>规范化编码格式：<c>auto</c> 优先 webp（体积约为 jpeg 的 60%），浏览器不支持时端点会回落。</summary>
    internal static string NormalizeFormat(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        "webp" => "webp",
        "png" => "png",
        "jpeg" or "jpg" => "jpg",
        _ => "jpg",
    };

    /// <summary>格式 → 缓存文件扩展名。</summary>
    internal static string ExtensionOfFormat(string format) => format switch
    {
        "webp" => "webp",
        "png" => "png",
        _ => "jpg",
    };

    /// <summary>格式 → 响应 Content-Type。</summary>
    public static string ContentTypeOf(string format) => format switch
    {
        "webp" => "image/webp",
        "png" => "image/png",
        _ => "image/jpeg",
    };

    /// <summary>
    /// 按魔数判定 Content-Type，判不出来才用请求的格式兜底。
    /// </summary>
    /// <remarks>
    /// 必须有这一步：请求 <c>fmt=webp</c> 而 ffmpeg 没编 libwebp 时会落成 jpeg，
    /// 若此时仍回 <c>image/webp</c>，浏览器会按 webp 解析一段 JPEG 字节 → 裂图。
    /// </remarks>
    public static string ContentTypeOfBytes(ReadOnlySpan<byte> data, string fallbackFormat)
    {
        if (data.Length >= 12
            && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
            && data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
            return "image/webp";
        if (data.Length >= 4 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            return "image/png";
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8)
            return "image/jpeg";
        return ContentTypeOf(fallbackFormat);
    }

    private static IEnumerable<string> Candidates(string format)
    {
        yield return format;
        if (format != "jpg") yield return "jpg";
    }

    private static string ExtensionOf(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot < 0 || dot == name.Length - 1 ? string.Empty : name[(dot + 1)..].ToLowerInvariant();
    }

    /// <summary>生成结果（成功带字节，失败带原因与 stderr 摘要）。</summary>
    private sealed record Generated(byte[]? Data, string Format, string? Reason, string? Detail);

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
    }
}
