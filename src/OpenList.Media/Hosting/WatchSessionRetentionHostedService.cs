using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenList.Core.Interfaces;
using OpenList.Media.Domain.Abstractions;

namespace OpenList.Media.Hosting;

/// <summary>
/// 观看会话留存清理：按设置项 <c>media_watch_retention_days</c> 删除过旧的
/// <c>x_media_watch_sessions</c>。天数为 0/非法值时完全不清理（保留全部历史）。
///
/// <para>
/// 默认值 3650 天与口味页 <c>?days=</c> 的最大可取窗口（<c>TasteWindow.MaxDays</c>）对齐：
/// 默认配置下不会删掉用户还能查到的任何会话。管理员显式调小才会让更早的历史不可查。
/// </para>
/// <para>
/// 每天一轮：留存口径本身以「天」为单位，清理晚几小时没有意义；删除要扫描整张表的 Id/LastAt，
/// 没必要高频。启动后先等 5 分钟，避开启动期资源争抢。
/// </para>
/// </summary>
public sealed class WatchSessionRetentionHostedService : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromDays(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WatchSessionRetentionHostedService> _logger;

    /// <summary>创建留存清理后台服务。</summary>
    public WatchSessionRetentionHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<WatchSessionRetentionHostedService> logger)
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
                await PurgeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "watch session retention tick failed");
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

    /// <summary>按设置的天数清理一轮。0 或非法值表示永久保留。</summary>
    private async Task PurgeAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingService>();
        var sessions = scope.ServiceProvider.GetRequiredService<IWatchSessionService>();

        var resp = await settings.GetAsync("media_watch_retention_days", ct);
        var days = int.TryParse(resp.Code == 200 ? resp.Data?.Value : null, out var d) ? d : 0;
        if (days <= 0) return;

        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        var removed = await sessions.PruneAsync(cutoff, ct);
        if (removed > 0)
            _logger.LogInformation(
                "pruned {Count} watch sessions older than {Days} days (cutoff {Cutoff:o})",
                removed, days, cutoff);
    }
}
