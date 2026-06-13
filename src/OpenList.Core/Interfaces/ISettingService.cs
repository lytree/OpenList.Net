using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

public interface ISettingService
{
    Task<string?> GetSettingAsync(string key, CancellationToken ct = default);
    Task<SettingItem?> GetSettingItemAsync(string key, CancellationToken ct = default);
    Task<List<SettingItem>> GetSettingsInKeysAsync(List<string> keys, CancellationToken ct = default);
    Task<List<SettingItem>> ListSettingsAsync(CancellationToken ct = default);
    Task<List<SettingItem>> ListSettingsByGroupsAsync(List<int> groups, CancellationToken ct = default);
    Task SaveSettingsAsync(List<SettingItem> settings, CancellationToken ct = default);
    Task DeleteSettingAsync(string key, CancellationToken ct = default);
    Task<List<SettingItem>> DefaultSettingsAsync(CancellationToken ct = default);
    Task<List<SettingItem>> DefaultSettingsByGroupsAsync(List<int> groups, CancellationToken ct = default);
    Task<string> ResetTokenAsync(CancellationToken ct = default);
    bool GetBool(string key);
    string? GetString(string key);
}
