using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

public class SettingService : ISettingService
{
    private readonly OpenListDbContext _db;
    private readonly ConcurrentDictionary<string, string> _cache = new();

    public SettingService(OpenListDbContext db) => _db = db;

    public async Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var item = await _db.Settings.FindAsync([key], ct);
        if (item != null) _cache[key] = item.Value ?? "";
        return item?.Value;
    }

    public async Task<SettingItem?> GetSettingItemAsync(string key, CancellationToken ct = default)
        => await _db.Settings.FindAsync([key], ct);

    public async Task<List<SettingItem>> GetSettingsInKeysAsync(List<string> keys, CancellationToken ct = default)
        => await _db.Settings.Where(s => keys.Contains(s.Key)).OrderBy(s => s.Group).ThenBy(s => s.Index).ToListAsync(ct);

    public async Task<List<SettingItem>> ListSettingsAsync(CancellationToken ct = default)
        => await _db.Settings.OrderBy(s => s.Group).ThenBy(s => s.Index).ToListAsync(ct);

    public async Task<List<SettingItem>> ListSettingsByGroupsAsync(List<int> groups, CancellationToken ct = default)
    {
        var groupEnums = groups.Select(g => (SettingGroup)g).ToList();
        return await _db.Settings.Where(s => groupEnums.Contains(s.Group)).OrderBy(s => s.Group).ThenBy(s => s.Index).ToListAsync(ct);
    }

    public async Task SaveSettingsAsync(List<SettingItem> settings, CancellationToken ct = default)
    {
        foreach (var setting in settings)
        {
            var existing = await _db.Settings.FindAsync([setting.Key], ct);
            if (existing != null)
            {
                existing.Value = setting.Value;
                existing.Help = setting.Help;
                existing.Type = setting.Type;
                existing.Options = setting.Options;
                existing.Group = setting.Group;
                existing.Flag = setting.Flag;
                existing.Index = setting.Index;
            }
            else
            {
                _db.Settings.Add(setting);
            }
            _cache[setting.Key] = setting.Value ?? "";
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteSettingAsync(string key, CancellationToken ct = default)
    {
        var item = await _db.Settings.FindAsync([key], ct);
        if (item != null)
        {
            _db.Settings.Remove(item);
            await _db.SaveChangesAsync(ct);
            _cache.TryRemove(key, out _);
        }
    }

    public async Task<List<SettingItem>> DefaultSettingsAsync(CancellationToken ct = default)
    {
        var defaults = GetDefaultSettings();
        return defaults;
    }

    public Task<List<SettingItem>> DefaultSettingsByGroupsAsync(List<int> groups, CancellationToken ct = default)
    {
        var groupEnums = groups.Select(g => (SettingGroup)g).ToList();
        var defaults = GetDefaultSettings().Where(s => groupEnums.Contains(s.Group)).ToList();
        return Task.FromResult(defaults);
    }

    public async Task<string> ResetTokenAsync(CancellationToken ct = default)
    {
        var newSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var item = await _db.Settings.FindAsync(["jwt_secret"], ct);
        if (item != null)
        {
            item.Value = newSecret;
            await _db.SaveChangesAsync(ct);
            _cache["jwt_secret"] = newSecret;
        }
        return newSecret;
    }

    public bool GetBool(string key)
    {
        var val = GetString(key);
        return val == "true" || val == "1";
    }

    public string? GetString(string key)
    {
        if (_cache.TryGetValue(key, out var val)) return val;
        return _db.Settings.Find(key)?.Value;
    }

    private static List<SettingItem> GetDefaultSettings() =>
    [
        new() { Key = "title", Value = "OpenList", Type = "string", Group = SettingGroup.Site, Flag = SettingFlag.Public, Index = 0 },
        new() { Key = "logo", Value = "", Type = "string", Group = SettingGroup.Site, Flag = SettingFlag.Public, Index = 1 },
        new() { Key = "favicon", Value = "", Type = "string", Group = SettingGroup.Site, Flag = SettingFlag.Public, Index = 2 },
        new() { Key = "site_url", Value = "", Type = "string", Group = SettingGroup.Site, Flag = SettingFlag.Public, Index = 3 },
        new() { Key = "announcement", Value = "", Type = "text", Group = SettingGroup.Site, Flag = SettingFlag.Public, Index = 4 },
        new() { Key = "text_types", Value = "", Type = "text", Group = SettingGroup.Preview, Flag = SettingFlag.Public, Index = 0 },
        new() { Key = "audio_types", Value = "", Type = "text", Group = SettingGroup.Preview, Flag = SettingFlag.Public, Index = 1 },
        new() { Key = "video_types", Value = "", Type = "text", Group = SettingGroup.Preview, Flag = SettingFlag.Public, Index = 2 },
        new() { Key = "hide_files", Value = "", Type = "text", Group = SettingGroup.Global, Flag = SettingFlag.Public, Index = 0 },
        new() { Key = "sign_all", Value = "false", Type = "bool", Group = SettingGroup.Global, Flag = SettingFlag.Public, Index = 1 },
        new() { Key = "privacy_regs", Value = "", Type = "text", Group = SettingGroup.Global, Flag = SettingFlag.Private, Index = 2 },
        new() { Key = "hide_storage_details", Value = "false", Type = "bool", Group = SettingGroup.Global, Flag = SettingFlag.Public, Index = 3 },
        new() { Key = "hide_storage_details_in_manage_page", Value = "false", Type = "bool", Group = SettingGroup.Global, Flag = SettingFlag.Private, Index = 4 },
        new() { Key = "jwt_secret", Value = "", Type = "string", Group = SettingGroup.Global, Flag = SettingFlag.Private, Index = 5 },
        new() { Key = "token_expires_in", Value = "48", Type = "number", Group = SettingGroup.Global, Flag = SettingFlag.Private, Index = 6 },
        new() { Key = "search_index", Value = "database", Type = "select", Options = "database,bleve,meilisearch,none", Group = SettingGroup.Index, Flag = SettingFlag.Public, Index = 0 },
        new() { Key = "auto_index", Value = "true", Type = "bool", Group = SettingGroup.Index, Flag = SettingFlag.Public, Index = 1 },
        new() { Key = "sso_client_id", Value = "", Type = "string", Group = SettingGroup.SSO, Flag = SettingFlag.Private, Index = 0 },
        new() { Key = "sso_client_secret", Value = "", Type = "string", Group = SettingGroup.SSO, Flag = SettingFlag.Private, Index = 1 },
        new() { Key = "sso_endpoint", Value = "", Type = "string", Group = SettingGroup.SSO, Flag = SettingFlag.Private, Index = 2 },
        new() { Key = "ldap_url", Value = "", Type = "string", Group = SettingGroup.LDAP, Flag = SettingFlag.Private, Index = 0 },
        new() { Key = "ldap_bind_dn", Value = "", Type = "string", Group = SettingGroup.LDAP, Flag = SettingFlag.Private, Index = 1 },
        new() { Key = "ldap_bind_password", Value = "", Type = "string", Group = SettingGroup.LDAP, Flag = SettingFlag.Private, Index = 2 },
        new() { Key = "ldap_search_filter", Value = "", Type = "string", Group = SettingGroup.LDAP, Flag = SettingFlag.Private, Index = 3 },
        new() { Key = "ldap_base_dn", Value = "", Type = "string", Group = SettingGroup.LDAP, Flag = SettingFlag.Private, Index = 4 },
    ];

}
