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
    /// 返回已启用的离线下载工具名称列表。当前为空数组，实际工具集成（aria2、qBittorrent 等）
    /// 将在后续阶段加入。
    /// </summary>
    public Task<Resp<string[]>> GetOfflineDownloadToolsAsync(CancellationToken ct = default)
    {
        // 返回已启用的离线下载工具名。实际工具集成（aria2、qBittorrent 等）将在后续阶段加入。
        var tools = Array.Empty<string>();
        return Task.FromResult(Resp<string[]>.Ok(tools));
    }

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
