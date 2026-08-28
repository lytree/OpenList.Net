using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Interfaces;

namespace OpenList.Server.Middleware;

/// <summary>
/// 共享限速器管理（单例）。持有 4 个与 Go 的 <c>internal/stream</c> 全局限速器对应的
/// <see cref="RateLimiter"/>：客户端下载/上传、服务端下载/上传，由数据库设置
/// <c>max_client_download_speed</c> 等（KiB/s，&lt;=0 不限速）驱动。
/// <see cref="EnsureFreshAsync"/> 在首次使用及此后每隔一段时间重新读取设置，
/// 使管理员修改流量限制后无需重启即可生效（对应 Go 的 RegisterSettingChangingCallback）。
/// </summary>
public sealed class RateLimitManager
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopes;
    private readonly object _lock = new();
    private long _lastRefreshTicks;
    private bool _initialized;

    public RateLimitManager(IServiceScopeFactory scopes)
    {
        _scopes = scopes;
    }

    /// <summary>客户端（用户侧）下载限速器。</summary>
    public RateLimiter ClientDownload { get; } = new();

    /// <summary>客户端（用户侧）上传限速器。</summary>
    public RateLimiter ClientUpload { get; } = new();

    /// <summary>服务端（代理/归档）下载限速器。</summary>
    public RateLimiter ServerDownload { get; } = new();

    /// <summary>服务端（代理/归档）上传限速器。</summary>
    public RateLimiter ServerUpload { get; } = new();

    /// <summary>
    /// 确保限速器已从设置初始化，并周期性刷新（每 <see cref="RefreshInterval"/> 一次）。
    /// 读取失败时维持当前限速，绝不抛出。
    /// </summary>
    public async Task EnsureFreshAsync(CancellationToken ct = default)
    {
        bool need;
        lock (_lock)
        {
            var now = Environment.TickCount64;
            need = !_initialized || now - _lastRefreshTicks >= (long)RefreshInterval.TotalMilliseconds;
            if (need)
            {
                _initialized = true;
                _lastRefreshTicks = now;
            }
        }
        if (!need) return;

        try
        {
            using var scope = _scopes.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<ISettingService>();
            ClientDownload.SetLimit(await GetKibAsync(svc, "max_client_download_speed", ct), 0);
            ClientUpload.SetLimit(await GetKibAsync(svc, "max_client_upload_speed", ct), 0);
            ServerDownload.SetLimit(await GetKibAsync(svc, "max_server_download_speed", ct), 0);
            ServerUpload.SetLimit(await GetKibAsync(svc, "max_server_upload_speed", ct), 0);
        }
        catch
        {
            // 设置读取失败则维持当前限速，不阻塞请求。
        }
    }

    /// <summary>读取 KiB/s 设置并换算为字节/秒；缺失或非数字时回退为 0（不限速）。</summary>
    private static async Task<int> GetKibAsync(ISettingService svc, string key, CancellationToken ct)
    {
        try
        {
            var r = await svc.GetAsync(key, ct);
            if (r.Code == 200 && r.Data is not null && int.TryParse(r.Data.Value, out var kib))
                return kib <= 0 ? 0 : kib * 1024;
        }
        catch
        {
            // ignore
        }
        return 0;
    }
}
