using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Server.Hosting;

/// <summary>
/// 媒体库自动扫描后台服务：按设置项 <c>media_auto_scan_interval_hours</c> 定期扫描
/// <see cref="MediaLibrary.AutoScan"/> 为 true 的库。间隔为 0 时完全关闭（默认），
/// 避免在没有配置的情况下对大容量网盘产生意外流量。
/// </summary>
public sealed class MediaAutoScanHostedService : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMediaScanService _scans;
    private readonly ILogger<MediaAutoScanHostedService> _logger;

    /// <summary>创建自动扫描服务。</summary>
    public MediaAutoScanHostedService(
        IServiceScopeFactory scopeFactory,
        IMediaScanService scans,
        ILogger<MediaAutoScanHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _scans = scans;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动后先等一轮，避免和服务启动抢资源。
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanDueLibrariesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "media auto scan tick failed");
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

    /// <summary>扫描所有到期的媒体库。间隔从设置读取，缺省/非法值为 0（关闭）。</summary>
    private async Task ScanDueLibrariesAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingService>();
        var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();

        var intervalResp = await settings.GetAsync("media_auto_scan_interval_hours", ct);
        var hours = int.TryParse(intervalResp.Code == 200 ? intervalResp.Data?.Value : null, out var h) ? h : 0;
        if (hours <= 0) return;

        var threshold = DateTimeOffset.UtcNow.AddHours(-hours);
        var due = await db.MediaLibraries.AsNoTracking()
            .Where(l => l.AutoScan && (l.LastScanAt == null || l.LastScanAt < threshold))
            .Select(l => l.Id)
            .ToListAsync(ct);

        // 用管理员身份触发（扫描本身不涉及个人数据）。
        var systemUser = new User { Id = 0, Username = "media-scheduler", Role = UserRole.Admin };
        foreach (var id in due)
        {
            var resp = await _scans.StartAsync(id, systemUser, ct);
            if (resp.Code == 200)
                _logger.LogInformation("media library {LibraryId} auto scan started", id);
        }
    }
}
