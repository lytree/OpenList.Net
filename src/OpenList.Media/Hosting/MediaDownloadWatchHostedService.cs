using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenList.Media.Domain.Abstractions;

namespace OpenList.Media.Hosting;

/// <summary>
/// 下载状态轮询宿主：每 30 秒把「下载中」的行与下载器的任务状态对齐，
/// 下载完成的行触发一次库扫描并把已入库的作品回填到想看行。
/// <para>
/// 为什么放在宿主而不是每次请求时顺手刷：下载器是独立进程，状态只随时间变化，
/// 没有任何请求会「自然地」触发它。30 秒是进度条的体感下限，也比各下载器的
/// RPC 轮询（2 秒）宽松得多，不会打爆 aria2/qBittorrent。
/// </para>
/// </summary>
public sealed class MediaDownloadWatchHostedService : BackgroundService
{
    /// <summary>轮询间隔。</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    /// <summary>启动后等待：让数据库迁移与任务恢复先完成。</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MediaDownloadWatchHostedService> _logger;

    /// <summary>创建下载轮询宿主。</summary>
    public MediaDownloadWatchHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<MediaDownloadWatchHostedService> logger)
    {
        _scopeFactory = scopeFactory;
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
                using var scope = _scopeFactory.CreateScope();
                var downloads = scope.ServiceProvider.GetRequiredService<IMediaDownloadService>();
                var changed = await downloads.RefreshAsync(stoppingToken);
                if (changed > 0) _logger.LogInformation("media download states updated: {Count}", changed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "media download watch tick failed");
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
}
