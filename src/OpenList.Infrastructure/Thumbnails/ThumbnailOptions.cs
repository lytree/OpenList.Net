using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;

namespace OpenList.Infrastructure.Thumbnails;

/// <summary>
/// 缩略图服务的只读配置快照（由设置项映射而来）。
/// <para>
/// 用快照而不是每次读设置：<c>fs/list</c> 的每个条目都要判定一次「能不能出图」，
/// 逐条查库会直接把列表拖慢。快照由 <see cref="ThumbnailOptionsProvider"/> 后台定时刷新。
/// </para>
/// </summary>
public sealed record ThumbnailOptions
{
    /// <summary>生成器版本：改算法/参数语义时 +1，让老缓存自然失效。</summary>
    public const int GeneratorVersion = 1;

    /// <summary>总开关；关闭时列表不下发 thumb、端点直接 415。</summary>
    public bool Enabled { get; init; }

    /// <summary>列表下发 thumb 时请求的边长（像素，正方形）。</summary>
    public int MaxSize { get; init; } = 320;

    /// <summary>编码质量（1–100）。</summary>
    public int Quality { get; init; } = 80;

    /// <summary>允许生成的扩展名（不含点）；空集合表示「按内置类型表」。</summary>
    public HashSet<string> AllowedExtensions { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>缓存目录（绝对路径）。</summary>
    public string CacheFolder { get; init; } = string.Empty;

    /// <summary>缓存容量上限（字节）。</summary>
    public long CacheMaxBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>整读类（图片 / 音频）源文件大小上限（字节）。</summary>
    public long MaxSourceBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>视频抽帧位置：秒数（<c>15</c>）或百分比（<c>10%</c>）。</summary>
    public string VideoPosition { get; init; } = "10%";

    /// <summary>视频子开关；关闭时不解析 ffmpeg，也不接受视频缩略图请求。</summary>
    public bool VideoEnabled { get; init; } = true;

    /// <summary>全局并发上限；0 表示按 CPU 核数自动取值。</summary>
    public int Concurrency { get; init; }

    /// <summary>ffmpeg 可执行文件路径；空表示按 PATH / 环境变量查找。</summary>
    public string? FfmpegPath { get; init; }

    /// <summary>解析后的全局并发上限。</summary>
    public int EffectiveConcurrency => Concurrency > 0 ? Concurrency : Math.Max(2, Environment.ProcessorCount);

    /// <summary>该扩展名是否在允许集合内（集合为空时放行，交给内置类型表判定）。</summary>
    public bool AllowsExtension(string extension)
        => AllowedExtensions.Count == 0 || AllowedExtensions.Contains(extension);

    /// <summary>关闭状态下的默认实例（未读到设置前的兜底）。</summary>
    public static ThumbnailOptions Disabled(string cacheFolder) => new() { Enabled = false, CacheFolder = cacheFolder };

    /// <summary>
    /// 把设置项字典映射成快照。缺键、格式错误的项一律退回默认值，不抛异常 ——
    /// 一个拼错的设置项不该让整个列表接口 500。
    /// </summary>
    public static ThumbnailOptions FromSettings(IReadOnlyDictionary<string, string> settings, string dataDir)
    {
        var cacheFolder = Get(settings, "thumb_cache_folder");
        if (string.IsNullOrWhiteSpace(cacheFolder))
            cacheFolder = Path.Combine(dataDir, "thumbnails");
        else if (!Path.IsPathRooted(cacheFolder))
            cacheFolder = Path.GetFullPath(Path.Combine(dataDir, cacheFolder));

        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in Get(settings, "thumb_types").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            types.Add(raw.TrimStart('.'));

        return new ThumbnailOptions
        {
            Enabled = GetBool(settings, "thumb_enable"),
            MaxSize = Clamp(GetInt(settings, "thumb_max_size", 320), 16, 1024),
            Quality = Clamp(GetInt(settings, "thumb_quality", 80), 1, 100),
            AllowedExtensions = types,
            CacheFolder = cacheFolder,
            CacheMaxBytes = Math.Max(16, GetInt(settings, "thumb_cache_max_mb", 2048)) * 1024L * 1024L,
            MaxSourceBytes = Math.Max(1, GetInt(settings, "thumb_max_source_mb", 64)) * 1024L * 1024L,
            VideoPosition = string.IsNullOrWhiteSpace(Get(settings, "thumb_video_position"))
                ? "10%"
                : Get(settings, "thumb_video_position").Trim(),
            VideoEnabled = GetBool(settings, "thumb_video_enable", defaultValue: true),
            Concurrency = Math.Max(0, GetInt(settings, "thumb_concurrency", 0)),
            FfmpegPath = string.IsNullOrWhiteSpace(Get(settings, "thumb_ffmpeg_path")) ? null : Get(settings, "thumb_ffmpeg_path").Trim(),
        };
    }

    private static string Get(IReadOnlyDictionary<string, string> settings, string key)
        => settings.TryGetValue(key, out var value) ? value : string.Empty;

    private static bool GetBool(IReadOnlyDictionary<string, string> settings, string key, bool defaultValue = false)
        => settings.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : defaultValue;

    private static int GetInt(IReadOnlyDictionary<string, string> settings, string key, int defaultValue)
        => settings.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : defaultValue;

    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
}

/// <summary>
/// 设置项 → <see cref="ThumbnailOptions"/> 快照的持有者（单例）。
/// <para>
/// 刷新方式：启动后后台循环每 20 秒重读一次设置表。<b>刻意不做「每次请求读库」</b>——
/// 那会把 <c>fs/list</c> 变成 N+1 查询；也刻意不在 <c>Build()</c> 里阻塞等待，
/// 因为它跑在同步的列表映射里，阻塞会有死锁风险。
/// </para>
/// </summary>
public sealed class ThumbnailOptionsProvider : IDisposable
{
    /// <summary>快照有效期。</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(20);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ThumbnailOptionsProvider> _log;
    private readonly string _dataDir;
    private readonly CancellationTokenSource _stop = new();
    private volatile ThumbnailOptions _current;

    /// <summary>创建提供者，并启动后台刷新循环。</summary>
    /// <param name="scopes">作用域工厂（读取 scoped 的 <see cref="ISettingService"/>）。</param>
    /// <param name="appConfig">应用配置（用于确定 <c>{dataDir}</c>）。</param>
    /// <param name="log">日志器。</param>
    public ThumbnailOptionsProvider(
        IServiceScopeFactory scopes,
        AppConfiguration appConfig,
        ILogger<ThumbnailOptionsProvider> log)
    {
        _scopes = scopes;
        _log = log;
        _dataDir = ResolveDataDir(appConfig);
        // 未读到设置前按「关闭」处理：升级瞬间不会凭空多出解码负载。
        _current = ThumbnailOptions.Disabled(Path.Combine(_dataDir, "thumbnails"));
        _ = Task.Run(RefreshLoopAsync);
    }

    /// <summary>当前快照（永不为 null）。</summary>
    public ThumbnailOptions Current => _current;

    /// <summary>立即重读一次设置（供启动流程与测试调用）。</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var settings = scope.ServiceProvider.GetService<ISettingService>();
            if (settings is null) return;
            var resp = await settings.ListAsync(ct).ConfigureAwait(false);
            if (resp.Code != 200 || resp.Data is null) return;
            var dict = resp.Data.ToDictionary(s => s.Key, s => s.Value ?? string.Empty, StringComparer.Ordinal);
            _current = ThumbnailOptions.FromSettings(dict, _dataDir);
        }
        catch (Exception ex)
        {
            // 设置读不出来时保留上一份快照：缩略图不生成可以，列表接口不能因此报错。
            _log.LogDebug(ex, "thumbnail: refresh options failed");
        }
    }

    private async Task RefreshLoopAsync()
    {
        await RefreshAsync(_stop.Token).ConfigureAwait(false);
        using var timer = new PeriodicTimer(RefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                await RefreshAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* 进程退出 */ }
    }

    /// <summary>dataDir 的解析与 Program.cs 一致：数据库文件所在目录。</summary>
    private static string ResolveDataDir(AppConfiguration appConfig)
    {
        var dbFile = appConfig.Database?.DBFile;
        if (string.IsNullOrWhiteSpace(dbFile)) return AppContext.BaseDirectory;
        return Path.GetDirectoryName(Path.GetFullPath(dbFile)) ?? AppContext.BaseDirectory;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
