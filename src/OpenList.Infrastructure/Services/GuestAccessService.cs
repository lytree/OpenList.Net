using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Interfaces;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 访客访问策略的默认实现：读取设置表里的两个开关，带 3 秒内存缓存。
/// <para>
/// 为什么是单例 + <see cref="IServiceScopeFactory"/>：判定发生在**每一个**匿名请求上
/// （媒体一页 30 张海报就是 30 次判定），而 <c>ISettingService</c> / <c>DbContext</c> 都是
/// scoped，把策略做成 scoped 等于每个请求都查一次库。这里只在缓存过期时开一个作用域读两行，
/// 与 <see cref="SignService"/> 的做法一致（同样是「每个请求都要判、值又很少变」的场景）。
/// </para>
/// <para>
/// 缓存的代价是「保存设置后最多 3 秒才生效」；管理端保存走
/// <see cref="InvalidateCache"/> 可以立刻生效，测试也用它避免等待。
/// </para>
/// </summary>
public sealed class GuestAccessService : IGuestAccessService
{
    /// <summary>媒体域访客开关（唯一真源：设置表同名列）。</summary>
    private const string MediaKey = "media_guest_access";

    /// <summary>首页（文件浏览）访客开关。</summary>
    private const string HomeKey = "home_guest_access";

    /// <summary>判定结果的缓存时长。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(3);

    private readonly IServiceScopeFactory? _scopes;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>缓存快照；初值就是「设置表里一条都没有」时的默认值。</summary>
    private volatile Snapshot _snapshot = Snapshot.Default;
    private DateTimeOffset _freshUntil = DateTimeOffset.MinValue;

    /// <summary>
    /// 创建访客访问策略实例。
    /// </summary>
    /// <param name="scopes">DI 作用域工厂（用于读取设置表）。为空时永远返回默认值。</param>
    /// <param name="time">时间源（可选，默认系统时钟）。</param>
    public GuestAccessService(IServiceScopeFactory? scopes = null, TimeProvider? time = null)
    {
        _scopes = scopes;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<bool> CanBrowseMediaAsync(CancellationToken ct = default)
        => (await GetSnapshotAsync(ct)).MediaAllowsGuest;

    /// <inheritdoc />
    public async Task<bool> CanBrowseHomeAsync(CancellationToken ct = default)
        => (await GetSnapshotAsync(ct)).HomeAllowsGuest;

    /// <inheritdoc />
    public void InvalidateCache() => _freshUntil = DateTimeOffset.MinValue;

    private async Task<Snapshot> GetSnapshotAsync(CancellationToken ct)
    {
        if (_scopes is null) return Snapshot.Default;
        if (_time.GetUtcNow() < _freshUntil) return _snapshot;

        await _refreshGate.WaitAsync(ct);
        try
        {
            if (_time.GetUtcNow() < _freshUntil) return _snapshot;

            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetService<OpenListDbContext>();
            if (db is null) return _snapshot;

            // 只取这两行，别把 130+ 条设置全拉回来。
            var values = await db.Settings.AsNoTracking()
                .Where(s => s.Key == MediaKey || s.Key == HomeKey)
                .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

            // 键缺失（老库没跑过种子 / 被手工删了）时回落到默认值：
            // 媒体开放、首页要登录 —— 与 DbInitializer 的种子保持一致。
            _snapshot = new Snapshot(
                MediaAllowsGuest: values.TryGetValue(MediaKey, out var media)
                    ? ParseBool(media)
                    : Snapshot.Default.MediaAllowsGuest,
                HomeAllowsGuest: values.TryGetValue(HomeKey, out var home)
                    ? ParseBool(home)
                    : Snapshot.Default.HomeAllowsGuest);
            _freshUntil = _time.GetUtcNow().Add(CacheTtl);
            return _snapshot;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>与 Go 的配置解析一致：<c>true</c> / <c>1</c> 为真，其余（含空串）为假。</summary>
    private static bool ParseBool(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");

    /// <summary>两个开关的快照。</summary>
    private sealed record Snapshot(bool MediaAllowsGuest, bool HomeAllowsGuest)
    {
        /// <summary>默认策略：媒体对游客开放，首页需要登录。</summary>
        internal static readonly Snapshot Default = new(MediaAllowsGuest: true, HomeAllowsGuest: false);
    }
}
