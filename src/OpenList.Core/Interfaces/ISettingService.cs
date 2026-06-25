using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

public interface ISettingService
{
    Task<Resp<List<SettingItem>>> ListAsync(CancellationToken ct = default);
    Task<Resp<SettingItem>> GetAsync(string key, CancellationToken ct = default);
    Task<Resp> SaveAsync(List<SettingItem> items, CancellationToken ct = default);
    Task<Resp> DeleteAsync(string key, CancellationToken ct = default);
    Task<Resp> DefaultAsync(string? group = null, CancellationToken ct = default);
    Task<Resp> ResetTokenAsync(CancellationToken ct = default);
    Task<Resp<Dictionary<string, string>>> GetPublicAsync(CancellationToken ct = default);
    Task<Resp<string[]>> GetOfflineDownloadToolsAsync(CancellationToken ct = default);
    Task<Resp<string[]>> GetArchiveExtensionsAsync(CancellationToken ct = default);
}

public sealed class OfflineDownloadTool
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string? Help { get; set; }
}
