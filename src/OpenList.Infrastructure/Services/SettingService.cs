using Microsoft.EntityFrameworkCore;
using OpenList.Core.Configuration;
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
    private readonly AppConfiguration _appConfig;
    private readonly TimeProvider _time;
    private List<SettingItem>? _cache;

    public SettingService(OpenListDbContext db, AppConfiguration appConfig, TimeProvider? time = null)
    {
        _db = db;
        _appConfig = appConfig;
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

    public async Task<Resp<List<SettingItem>>> DefaultAsync(string? group = null, CancellationToken ct = default)
    {
        // Restore default values for settings in the given group (or all if null),
        // then return the resulting SettingItem list (frontend sets its store from
        // the response data). Mirrors Go setting/default?group=.
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

        // Build the response from current DB state (preserves non-default fields
        // like Help/Options that may have been customized).
        var refreshed = await _db.Settings.AsNoTracking().Where(s => keys.Contains(s.Key)).ToListAsync(ct);
        var refreshedByKey = refreshed.ToDictionary(s => s.Key);
        var result = defaults.Select(d => refreshedByKey.TryGetValue(d.Key, out var item)
            ? item
            : new SettingItem { Key = d.Key, Value = d.Value, Type = d.Type, Flag = d.Flag, Group = d.Group, Options = d.Options }).ToList();
        return Resp<List<SettingItem>>.Ok(result);
    }

    public Task<Resp> ResetTokenAsync(CancellationToken ct = default)
    {
        // Rotate the JWT signing secret. Updates the singleton AppConfiguration
        // (shared by JwtService + SignService, which read it per-call) and persists
        // the new secret to <dataDir>/jwt_secret so it survives restarts. Existing
        // tokens become invalid immediately; the caller (AdminEndpoints) is
        // responsible for also updating the bearer validation parameters so the
        // middleware validates against the new secret.
        var newSecret = (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"))[..32];
        _appConfig.JwtSecret = newSecret;

        var dataDir = Path.GetDirectoryName(Path.GetFullPath(_appConfig.Database.DBFile)) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "jwt_secret"), newSecret);
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
