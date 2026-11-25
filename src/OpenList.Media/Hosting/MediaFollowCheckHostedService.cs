using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenList.Core.Interfaces;
using OpenList.Media.Domain.Abstractions;

namespace OpenList.Media.Hosting;

/// <summary>
/// 关注更新检查宿主：按设置项 <c>media_follow_check_interval_hours</c> 定期对全部关注做一次
/// 差集检查。间隔为 0 时关闭（默认）—— TMDB 调用要花配额，不能因为「有人关注了」就自动开始
/// 周期性出网。
/// <para>
/// 每 <see cref="Tick"/> 醒一次只为读设置：改间隔不需要重启进程。实际是否该跑由
/// <see cref="_lastRun"/> 判断，进程重启后的第一轮会在启动延迟后立刻跑。
/// 跑与不跑的名额由 <see cref="IMediaFollowService.CheckAllAsync"/> 自己抢，
/// 与手动「检查更新」天然互斥。
/// </para>
/// </summary>
public sealed class MediaFollowCheckHostedService : BackgroundService
{
    /// <summary>读设置的节拍：设置改了最多十分钟后生效。</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(10);

    /// <summary>启动后等待：避免和启动期的扫描/迁移抢资源。</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MediaFollowCheckHostedService> _logger;

    /// <summary>上一轮整轮检查的开始时间（进程内，重启即重置）。</summary>
    private DateTimeOffset? _lastRun;

    /// <summary>创建关注检查宿主。</summary>
    public MediaFollowCheckHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<MediaFollowCheckHostedService> logger)
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
                await RunIfDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "media follow check tick failed");
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

    /// <summary>到点就跑一轮；间隔从设置读取，缺省/非法值为 0（关闭）。</summary>
    private async Task RunIfDueAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingService>();

        var resp = await settings.GetAsync("media_follow_check_interval_hours", ct);
        var hours = int.TryParse(resp.Code == 200 ? resp.Data?.Value : null, out var h) ? h : 0;
        if (hours <= 0) return;

        var now = DateTimeOffset.UtcNow;
        if (_lastRun is { } last && now - last < TimeSpan.FromHours(hours)) return;

        var follow = scope.ServiceProvider.GetRequiredService<IMediaFollowService>();
        var added = await follow.CheckAllAsync(ct);
        _lastRun = now;
        if (added > 0) _logger.LogInformation("media follow check added {Count} new entries", added);
    }
}
