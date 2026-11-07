using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenList.Infrastructure.Thumbnails;

namespace OpenList.Server.Hosting;

/// <summary>
/// 缩略图磁盘缓存维护：
/// <list type="bullet">
/// <item>容量淘汰 —— 保持 <c>thumb_cache_max_mb</c> 的原有语义，超过上限按 LRU（mtime = 最后访问时间）删；</item>
/// <item>过期清理 —— 删除超过 <see cref="ThumbnailCache.UnusedRetention"/>（30 天）未访问的缓存文件。</item>
/// </list>
/// 每天一轮，启动后先等 5 分钟，避免与启动期请求争抢磁盘（30 天阈值下，晚几小时清理没有实际影响）。
/// </summary>
public sealed class ThumbnailCacheMaintenanceHostedService : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromDays(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);

    private readonly ThumbnailCache _cache;
    private readonly ThumbnailOptionsProvider _options;
    private readonly ILogger<ThumbnailCacheMaintenanceHostedService> _logger;

    /// <summary>创建缩略图缓存维护服务。</summary>
    public ThumbnailCacheMaintenanceHostedService(
        ThumbnailCache cache,
        ThumbnailOptionsProvider options,
        ILogger<ThumbnailCacheMaintenanceHostedService> logger)
    {
        _cache = cache;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MaintainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "thumbnail cache maintenance tick failed");
            }

            try
            {
                await Task.Delay(Tick, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task MaintainAsync(CancellationToken ct)
    {
        var options = _options.Current;
        await _cache.TrimAsync(options.CacheFolder, options.CacheMaxBytes, ct).ConfigureAwait(false);

        var removed = await _cache.SweepExpiredAsync(options.CacheFolder, ThumbnailCache.UnusedRetention, ct)
            .ConfigureAwait(false);
        if (removed > 0)
            _logger.LogInformation(
                "removed {Count} thumbnail cache files unused for {Days} days",
                removed, ThumbnailCache.UnusedRetention.TotalDays);
    }
}
