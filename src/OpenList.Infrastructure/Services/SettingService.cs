using Microsoft.EntityFrameworkCore;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 设置（键值对）增删改查，带内存缓存。公开设置通过 <see cref="GetPublicAsync"/>
/// 暴露（Flag 为 Public 的项）。对应 Go 参考实现 <c>internal/op/setting.go</c>。
/// </summary>
public sealed class SettingService : ISettingService
{
    private readonly OpenListDbContext _db;
    private readonly AppConfiguration _appConfig;
    private readonly TimeProvider _time;
    private List<SettingItem>? _cache;

    /// <summary>
    /// 创建设置服务实例。
    /// </summary>
    /// <param name="db">EF Core 数据库上下文。</param>
    /// <param name="appConfig">应用配置（用于轮换 JWT 密钥等）。</param>
    /// <param name="time">时间源（可选，默认系统时区）。</param>
    public SettingService(OpenListDbContext db, AppConfiguration appConfig, TimeProvider? time = null)
    {
        _db = db;
        _appConfig = appConfig;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// 列出全部设置（按 Key 升序），并填充内存缓存。
    /// </summary>
    public async Task<Resp<List<SettingItem>>> ListAsync(CancellationToken ct = default)
    {
        var list = await _db.Settings.AsNoTracking().OrderBy(s => s.Key).ToListAsync(ct);
        _cache = list;
        return Resp<List<SettingItem>>.Ok(list);
    }

    /// <summary>
    /// 按 Key 获取单个设置。
    /// </summary>
    public async Task<Resp<SettingItem>> GetAsync(string key, CancellationToken ct = default)
    {
        var item = await _db.Settings.FindAsync([key], ct);
        return item is null
            ? Resp<SettingItem>.Fail("setting not found", RespCodes.NotFound)
            : Resp<SettingItem>.Ok(item);
    }

    /// <summary>
    /// 批量保存设置：已存在的更新，不存在的新增；保存后失效缓存。
    /// </summary>
    public async Task<Resp> SaveAsync(List<SettingItem> items, CancellationToken ct = default)
    {
        var existing = await _db.Settings.ToDictionaryAsync(s => s.Key, ct);
        foreach (var item in items)
        {
            if (existing.TryGetValue(item.Key, out var db))
            {
                db.Value = item.Value;
                db.Help = item.Help;
                db.Type = item.Type;
                db.Flag = item.Flag;
                db.Options = item.Options;
                db.Group = item.Group;
            }
            else
            {
                _db.Settings.Add(item);
            }
        }
        await _db.SaveChangesAsync(ct);
        _cache = null;
        return Resp.Ok();
    }

    /// <summary>
    /// 删除指定 Key 的设置并失效缓存。
    /// </summary>
    public async Task<Resp> DeleteAsync(string key, CancellationToken ct = default)
    {
        var item = await _db.Settings.FindAsync([key], ct);
        if (item is null) return Resp.Fail("setting not found", RespCodes.NotFound);
        _db.Settings.Remove(item);
        await _db.SaveChangesAsync(ct);
        _cache = null;
        return Resp.Ok();
    }

    /// <summary>
    /// 将指定分组（或全部）的设置恢复为默认值，并返回恢复后的结果列表（前端据此更新状态）。
    /// 对应 Go 的 setting/default?group=。
    /// </summary>
    public async Task<Resp<List<SettingItem>>> DefaultAsync(string? group = null, CancellationToken ct = default)
    {
        // 恢复给定分组（group 为 null 时恢复全部）的默认值，并返回结果列表
        // （前端从响应数据更新其状态）。对应 Go 的 setting/default?group=。
        var defaults = DbInitializer.DefaultSettings;
        if (!string.IsNullOrEmpty(group) && int.TryParse(group, out var groupNum))
            defaults = defaults.Where(d => d.Group == groupNum).ToArray();
        if (defaults.Length == 0) return Resp<List<SettingItem>>.Ok([]);

        var keys = defaults.Select(d => d.Key).ToHashSet();
        var items = await _db.Settings.Where(s => keys.Contains(s.Key)).ToListAsync(ct);
        var byKey = items.ToDictionary(s => s.Key);
        foreach (var (key, value, _, _, _, _) in defaults)
        {
            if (byKey.TryGetValue(key, out var item))
                item.Value = value;
        }
        await _db.SaveChangesAsync(ct);
        _cache = null;

        // 基于当前数据库状态构建响应（保留可能已被自定义的非默认字段，如 Help/Options）。
        var refreshed = await _db.Settings.AsNoTracking().Where(s => keys.Contains(s.Key)).ToListAsync(ct);
        var refreshedByKey = refreshed.ToDictionary(s => s.Key);
        var result = defaults.Select(d => refreshedByKey.TryGetValue(d.Key, out var item)
            ? item
            : new SettingItem { Key = d.Key, Value = d.Value, Type = d.Type, Flag = d.Flag, Group = d.Group, Options = d.Options }).ToList();
        return Resp<List<SettingItem>>.Ok(result);
    }

    /// <summary>
    /// 轮换 JWT 签名密钥：更新单例 AppConfiguration（JwtService 与 SignService 每次调用时读取）
    /// 并持久化到 <c>dataDir/jwt_secret</c> 以在重启后保留。已有 Token 立即失效；
    /// 调用方（AdminEndpoints）还负责同步更新 Bearer 校验参数。
    /// </summary>
    public Task<Resp> ResetTokenAsync(CancellationToken ct = default)
    {
        // 轮换 JWT 签名密钥：更新单例 AppConfiguration（JwtService + SignService 每次调用读取）
        // 并持久化到 <dataDir>/jwt_secret 以便重启后保留。已有 Token 立即失效；
        // 调用方（AdminEndpoints）负责同步更新 Bearer 校验参数。
        var newSecret = (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"))[..32];
        _appConfig.JwtSecret = newSecret;

        var dataDir = Path.GetDirectoryName(Path.GetFullPath(_appConfig.Database.DBFile)) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "jwt_secret"), newSecret);
        return Task.FromResult(Resp.Ok());
    }

    /// <summary>
    /// 获取全部公开设置（Flag 为 Public）的键值字典，使用内存缓存加速。
    /// </summary>
    public async Task<Resp<Dictionary<string, string>>> GetPublicAsync(CancellationToken ct = default)
    {
        _cache ??= await _db.Settings.AsNoTracking().ToListAsync(ct);
        var dict = _cache
            .Where(s => s.Flag == SettingFlag.Public)
            .ToDictionary(s => s.Key, s => s.Value ?? string.Empty);
        return Resp<Dictionary<string, string>>.Ok(dict);
    }

    /// <summary>
    /// 返回已启用的离线下载工具名称列表。
    /// <para>
    /// 判定口径是「地址已配置」——与 <c>OfflineDownloadService</c> 各 <c>Build*Async</c>
    /// 构建工具时的 <c>Enabled</c> 判据一致（<c>aria2_uri</c> / <c>qbittorrent_url</c> /
    /// <c>transmission_uri</c> 非空即启用）。此前这里恒返回空数组，前端据此把提交按钮
    /// 全部禁用，离线下载在 UI 上完全不可用。
    /// </para>
    /// <para>
    /// <c>SimpleHttp</c> 是服务端自带的直连下载器（无需任何配置），因此排在最前、
    /// 永远可用；它不支持 BT/磁力链接，前端据此隐藏「边下边传」以外的差异提示。
    /// </para>
    /// </summary>
    public async Task<Resp<string[]>> GetOfflineDownloadToolsAsync(CancellationToken ct = default)
    {
        // 每次直查数据库而不是走 _cache：本服务是 Scoped，缓存只在单请求内有效，
        // 而公开端点可能被频繁调用，缓存反而增加「读到的不是最新配置」的风险面。
        var values = await _db.Settings.AsNoTracking()
            .Where(s => s.Key == "aria2_uri" || s.Key == "qbittorrent_url" || s.Key == "transmission_uri")
            .ToDictionaryAsync(s => s.Key, s => s.Value ?? "", ct);

        var tools = new List<string> { "SimpleHttp" };
        // 名称必须能被 OfflineDownloadService 的 switch 命中（它按小写比较，
        // 因此这里的大小写只影响 UI 展示，不影响路由）。
        if (IsConfigured(values, "aria2_uri")) tools.Add("aria2");
        if (IsConfigured(values, "qbittorrent_url")) tools.Add("qBittorrent");
        if (IsConfigured(values, "transmission_uri")) tools.Add("Transmission");
        return Resp<string[]>.Ok(tools.ToArray());
    }

    /// <summary>工具地址是否已配置（空/空白视为未启用）。</summary>
    private static bool IsConfigured(Dictionary<string, string> values, string key)
        => values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v);

    /// <summary>
    /// 返回归档读取器支持的压缩包扩展名列表。
    /// </summary>
    public Task<Resp<string[]>> GetArchiveExtensionsAsync(CancellationToken ct = default)
    {
        // 归档读取器支持的压缩包扩展名。
        var exts = new[] { ".zip", ".tar", ".gz", ".tgz", ".bz2", ".7z", ".rar" };
        return Task.FromResult(Resp<string[]>.Ok(exts));
    }
}
