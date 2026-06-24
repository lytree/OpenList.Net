using Microsoft.EntityFrameworkCore;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// Setting CRUD with in-memory cache. Mirrors OpenList Go <c>internal/op/setting.go</c>.
/// Public settings are exposed via <see cref="GetPublicAsync"/> (Flag == Public).
/// </summary>
public sealed class SettingService : ISettingService
{
    private readonly OpenListDbContext _db;
    private readonly TimeProvider _time;
    private List<SettingItem>? _cache;

    public SettingService(OpenListDbContext db, TimeProvider? time = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
    }

    public async Task<Resp<List<SettingItem>>> ListAsync(CancellationToken ct = default)
    {
        var list = await _db.Settings.AsNoTracking().OrderBy(s => s.Key).ToListAsync(ct);
        _cache = list;
        return Resp<List<SettingItem>>.Ok(list);
    }

    public async Task<Resp<SettingItem>> GetAsync(string key, CancellationToken ct = default)
    {
        var item = await _db.Settings.FindAsync([key], ct);
        return item is null
            ? Resp<SettingItem>.Fail("setting not found", RespCodes.NotFound)
            : Resp<SettingItem>.Ok(item);
    }

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

    public async Task<Resp> DeleteAsync(string key, CancellationToken ct = default)
    {
        var item = await _db.Settings.FindAsync([key], ct);
        if (item is null) return Resp.Fail("setting not found", RespCodes.NotFound);
        _db.Settings.Remove(item);
        await _db.SaveChangesAsync(ct);
        _cache = null;
        return Resp.Ok();
    }

    public Task<Resp> DefaultAsync(CancellationToken ct = default)
    {
        // Restore default settings — placeholder; full implementation in Phase 14.
        return Task.FromResult(Resp.Ok());
    }

    public Task<Resp> ResetTokenAsync(CancellationToken ct = default)
    {
        // Reset JWT token — placeholder; full implementation in Phase 14.
        return Task.FromResult(Resp.Ok());
    }

    public async Task<Resp<Dictionary<string, string>>> GetPublicAsync(CancellationToken ct = default)
    {
        _cache ??= await _db.Settings.AsNoTracking().ToListAsync(ct);
        var dict = _cache
            .Where(s => s.Flag == SettingFlag.Public)
            .ToDictionary(s => s.Key, s => s.Value ?? string.Empty);
        return Resp<Dictionary<string, string>>.Ok(dict);
    }

    public Task<Resp<string[]>> GetOfflineDownloadToolsAsync(CancellationToken ct = default)
    {
        // Returns enabled offline download tool names. Actual tool integration
        // (aria2, qBittorrent, etc.) will be added in a later phase.
        var tools = Array.Empty<string>();
        return Task.FromResult(Resp<string[]>.Ok(tools));
    }

    public Task<Resp<string[]>> GetArchiveExtensionsAsync(CancellationToken ct = default)
    {
        // Supported archive extensions for the archive reader.
        var exts = new[] { ".zip", ".tar", ".gz", ".tgz", ".bz2", ".7z", ".rar" };
        return Task.FromResult(Resp<string[]>.Ok(exts));
    }
}
