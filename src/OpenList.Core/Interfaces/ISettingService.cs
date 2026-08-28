using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// 网站设置管理服务接口。
/// </summary>
public interface ISettingService
{
    /// <summary>获取全部设置项。</summary>
    Task<Resp<List<SettingItem>>> ListAsync(CancellationToken ct = default);
    /// <summary>按 key 获取单个设置项。</summary>
    Task<Resp<SettingItem>> GetAsync(string key, CancellationToken ct = default);
    /// <summary>批量保存设置项。</summary>
    Task<Resp> SaveAsync(List<SettingItem> items, CancellationToken ct = default);
    /// <summary>删除单个设置项。</summary>
    Task<Resp> DeleteAsync(string key, CancellationToken ct = default);
    /// <summary>恢复默认设置；可指定分组。</summary>
    Task<Resp<List<SettingItem>>> DefaultAsync(string? group = null, CancellationToken ct = default);
    /// <summary>重置 JWT 令牌密钥（使已有令牌立即失效）。</summary>
    Task<Resp> ResetTokenAsync(CancellationToken ct = default);
    /// <summary>获取对外公开的设置字典。</summary>
    Task<Resp<Dictionary<string, string>>> GetPublicAsync(CancellationToken ct = default);
    /// <summary>获取已启用的离线下载工具名称列表。</summary>
    Task<Resp<string[]>> GetOfflineDownloadToolsAsync(CancellationToken ct = default);
    /// <summary>获取受支持的归档解压扩展名列表。</summary>
    Task<Resp<string[]>> GetArchiveExtensionsAsync(CancellationToken ct = default);
}

/// <summary>
/// 离线下载工具信息。
/// </summary>
public sealed class OfflineDownloadTool
{
    /// <summary>工具名称。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; }
    /// <summary>帮助说明，可为空。</summary>
    public string? Help { get; set; }
}
