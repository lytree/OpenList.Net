using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace OpenList.Infrastructure.Thumbnails;

/// <summary>一次 ffmpeg 调用的结果。</summary>
/// <param name="ExitCode">进程退出码；超时被杀或启动失败时为 -1。</param>
/// <param name="StandardOutput">stdout 的尾部（最多 4KB）。ffmpeg 把 <c>-version</c> / <c>-encoders</c> 输出到 stdout。</param>
/// <param name="StandardError">stderr 全文的尾部（最多 4KB），失败时用于定位原因。</param>
/// <param name="TimedOut">是否因为超过超时被强杀。</param>
/// <param name="Started">进程是否成功启动（false 表示找不到可执行文件 / 无执行权限）。</param>
public sealed record FfmpegRunResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut, bool Started)
{
    /// <summary>是否成功（正常退出且没超时）。</summary>
    public bool Success => Started && !TimedOut && ExitCode == 0;
}

/// <summary>
/// ffmpeg 子进程执行器：拼参数、抓 stderr、超时强杀、登记在跑的进程。
/// <para>
/// 设计对齐 Jellyfin 的进程治理（见 <c>docs/thumbnail-service-plan.md</c> §6.5 的修订记录）：
/// </para>
/// <list type="bullets">
/// <item>用 <see cref="ProcessStartInfo.ArgumentList"/> 逐个传参，<b>绝不</b>拼字符串 ——
/// <c>-vf scale=320:320:force_original_aspect_ratio=increase,crop=...</c> 里的空白与冒号
/// 会被子进程按空格拆开，FFmpeg 报「Unable to choose an output format」且退出码 -22，
/// 全程零日志（Jellyfin #18322 踩过的坑）。</item>
/// <item>stderr 一直读到进程结束，失败时把尾部记进日志 —— 压掉 stderr（<c>-v quiet</c>）
/// 是「缩略图不生成但查不出原因」的头号来源。</item>
/// <item>超时一律 <c>Kill(entireProcessTree: true)</c>：畸形文件会让 FFmpeg 无限 seek。</item>
/// <item>所有在跑的进程登记在案，进程退出时统一清理，避免容器重启留下孤儿。</item>
/// </list>
/// </summary>
public sealed class FfmpegProcessRunner : IDisposable
{
    /// <summary>stderr 只保留尾部：正常情况几行为空，失败时关键信息都在最后。</summary>
    private const int MaxStderrChars = 4096;

    private readonly ILogger<FfmpegProcessRunner> _log;
    private readonly ConcurrentDictionary<int, Process> _running = new();

    /// <summary>创建执行器。</summary>
    public FfmpegProcessRunner(ILogger<FfmpegProcessRunner> log) => _log = log;

    /// <summary>
    /// 运行一次 ffmpeg。
    /// </summary>
    /// <param name="exe">可执行文件（绝对路径或 PATH 中的名字）。</param>
    /// <param name="arguments">参数列表（不含可执行文件本身）。</param>
    /// <param name="timeout">超时；到点强杀整棵进程树。</param>
    /// <param name="ct">取消令牌；取消同样强杀。</param>
    public async Task<FfmpegRunResult> RunAsync(
        string exe,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start()) return new FfmpegRunResult(-1, string.Empty, "failed to start process", false, false);
        }
        catch (Exception ex)
        {
            // 最常见：ffmpeg 不在 PATH / 路径写错 / 没有执行权限。
            return new FfmpegRunResult(-1, string.Empty, ex.Message, false, false);
        }

        _running[process.Id] = process;
        try
        {
            // stdout 必须被读掉，否则管道缓冲区写满会让子进程阻塞（经典死锁）。
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillTree(process);
                var partial = await SafeAwait(stderrTask).ConfigureAwait(false);
                return new FfmpegRunResult(-1, string.Empty, Tail(partial), !ct.IsCancellationRequested, true);
            }

            var stdout = await SafeAwait(stdoutTask).ConfigureAwait(false);
            var stderr = await SafeAwait(stderrTask).ConfigureAwait(false);
            return new FfmpegRunResult(process.ExitCode, Tail(stdout), Tail(stderr), false, true);
        }
        finally
        {
            _running.TryRemove(process.Id, out _);
        }
    }

    /// <summary>强杀某个进程树；已退出时静默忽略。</summary>
    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 竞态：判活的瞬间进程自己退了，或已经没有权限 —— 都不需要处理。
        }
    }

    private static async Task<string> SafeAwait(Task<string> task)
    {
        try { return await task.ConfigureAwait(false); }
        catch { return string.Empty; }
    }

    private static string Tail(string text)
        => string.IsNullOrEmpty(text) ? string.Empty
            : text.Length <= MaxStderrChars ? text
            : text[^MaxStderrChars..];

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var pair in _running)
        {
            _log.LogDebug("thumbnail: killing leftover ffmpeg pid={Pid}", pair.Key);
            KillTree(pair.Value);
        }
        _running.Clear();
    }
}

/// <summary>
/// ffmpeg / ffprobe 的定位与能力探测。
/// <para>
/// 查找顺序对齐 Jellyfin <c>MediaEncoder.SetFFmpegPath()</c> 的三级回退，但把「配置」换成
/// 本项目的设置项（没有 encoding.xml）：
/// </para>
/// <list type="number">
/// <item>设置项 <c>thumb_ffmpeg_path</c>（管理员显式指定，最高优先级）；</item>
/// <item>环境变量 <c>OPENLIST_FFMPEG</c>（Docker 镜像里用 <c>ENV</c> 指定，免配界面）；</item>
/// <item>字面量 <c>ffmpeg</c>，交给系统 PATH 解析。</item>
/// </list>
/// <para>
/// ffprobe 不单独搜，直接把路径里的文件名换掉（<c>ffmpeg.exe</c> → <c>ffprobe.exe</c>）；
/// 找不到就退化为「按百分比定位时用固定秒数」。
/// </para>
/// <para>
/// 探测结果带缓存：成功缓存 10 分钟，失败缓存 1 分钟。否则每个缩略图请求都要先 fork 一个
/// <c>ffmpeg -version</c>，比生成本身还贵。
/// </para>
/// </summary>
public sealed class FfmpegTool
{
    /// <summary>环境变量名（Docker / 桌面版打包用）。</summary>
    public const string EnvironmentVariable = "OPENLIST_FFMPEG";

    private static readonly Regex VersionRegex = new(@"ffmpeg version (\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SuccessTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromMinutes(1);

    private readonly FfmpegProcessRunner _runner;
    private readonly ILogger<FfmpegTool> _log;
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private ProbeResult? _cached;

    /// <summary>创建定位器。</summary>
    public FfmpegTool(FfmpegProcessRunner runner, ILogger<FfmpegTool> log)
    {
        _runner = runner;
        _log = log;
    }

    /// <summary>
    /// 解析可用的 ffmpeg；不存在或不可执行时返回 <c>null</c>。
    /// </summary>
    /// <param name="configuredPath">设置项 <c>thumb_ffmpeg_path</c> 的值（可空）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<FfmpegInfo?> ResolveAsync(string? configuredPath, CancellationToken ct)
    {
        var candidate = PickCandidate(configuredPath);
        var cached = _cached;
        if (cached is not null && cached.Executable == candidate && cached.ExpireAt > DateTimeOffset.UtcNow)
            return cached.Info;

        await _probeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cached = _cached;
            if (cached is not null && cached.Executable == candidate && cached.ExpireAt > DateTimeOffset.UtcNow)
                return cached.Info;

            var info = await ProbeAsync(candidate, ct).ConfigureAwait(false);
            _cached = new ProbeResult(candidate, info, DateTimeOffset.UtcNow + (info is null ? FailureTtl : SuccessTtl));
            if (info is null)
                _log.LogInformation("thumbnail: ffmpeg not available (candidate={Candidate})", candidate);
            else
                _log.LogInformation("thumbnail: ffmpeg {Version} at {Executable} (webp={Webp})",
                    info.Version, info.Executable, info.SupportsWebp);
            return info;
        }
        finally
        {
            _probeGate.Release();
        }
    }

    /// <summary>候选路径：设置项 → 环境变量 → PATH 里的 <c>ffmpeg</c>。</summary>
    private static string PickCandidate(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath)) return configuredPath.Trim();
        var fromEnv = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();
        return OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
    }

    private async Task<FfmpegInfo?> ProbeAsync(string candidate, CancellationToken ct)
    {
        var version = await _runner.RunAsync(candidate, new[] { "-hide_banner", "-nostdin", "-version" }, ProbeTimeout, ct)
            .ConfigureAwait(false);
        if (!version.Success)
        {
            if (version.Started)
                _log.LogWarning("thumbnail: ffmpeg -version failed (exit={Exit}) {Stderr}", version.ExitCode, Shorten(version.StandardError));
            return null;
        }

        var match = VersionRegex.Match(version.StandardOutput);
        var build = match.Success ? match.Groups[1].Value : "unknown";

        // libwebp 是否可用决定 fmt=webp 能不能落，落不了就退化 jpeg（一次探测，不靠版本号猜）。
        var encoders = await _runner.RunAsync(candidate, new[] { "-hide_banner", "-nostdin", "-encoders" }, ProbeTimeout, ct)
            .ConfigureAwait(false);
        var supportsWebp = encoders.Success && encoders.StandardOutput.Contains("libwebp", StringComparison.OrdinalIgnoreCase);

        return new FfmpegInfo(candidate, SiblingProbe(candidate), build, supportsWebp);
    }

    /// <summary>ffprobe 用同一目录下的同名可执行文件；找不到就返回 null（调用方退化）。</summary>
    private static string? SiblingProbe(string ffmpeg)
    {
        try
        {
            var dir = Path.GetDirectoryName(ffmpeg);
            var name = Path.GetFileNameWithoutExtension(ffmpeg);
            var ext = Path.GetExtension(ffmpeg);
            if (name.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase))
                name = "ffprobe";
            var probe = string.IsNullOrEmpty(dir) ? name + ext : Path.Combine(dir, name + ext);
            return File.Exists(probe) || string.IsNullOrEmpty(dir) ? probe : null;
        }
        catch { return null; }
    }

    private static string Shorten(string text)
        => string.IsNullOrEmpty(text) ? string.Empty
            : text.Length <= 400 ? text.Trim()
            : text[^400..].Trim();

    private sealed record ProbeResult(string Executable, FfmpegInfo? Info, DateTimeOffset ExpireAt);
}

/// <summary>探测到的 ffmpeg 信息。</summary>
/// <param name="Executable">可执行文件路径或名字。</param>
/// <param name="ProbeExecutable">ffprobe 路径；为空表示不可用。</param>
/// <param name="Version">版本串（<c>-version</c> 输出里的第一个 token）。</param>
/// <param name="SupportsWebp">是否带 <c>libwebp</c> 编码器。</param>
public sealed record FfmpegInfo(string Executable, string? ProbeExecutable, string Version, bool SupportsWebp);
