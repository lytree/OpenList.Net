namespace OpenList.Core.Models;

using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

public class Storage
{
    public uint Id { get; set; }
    [JsonPropertyName("mount_path")]
    public string MountPath { get; set; } = string.Empty;
    public int Order { get; set; }
    public string Driver { get; set; } = string.Empty;
    [JsonPropertyName("cache_expiration")]
    public int CacheExpiration { get; set; }
    [JsonPropertyName("custom_cache_policies")]
    public string? CustomCachePolicies { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Addition { get; set; } = "{}";
    public string? Remark { get; set; }
    public DateTime Modified { get; set; } = DateTime.UtcNow;
    public bool Disabled { get; set; }
    [JsonPropertyName("disable_index")]
    public bool DisableIndex { get; set; }
    [JsonPropertyName("enable_sign")]
    public bool EnableSign { get; set; }

    // Sort
    [JsonPropertyName("order_by")]
    public string? OrderBy { get; set; }
    [JsonPropertyName("order_direction")]
    public string? OrderDirection { get; set; }
    [JsonPropertyName("extract_folder")]
    public string? ExtractFolder { get; set; }

    // Proxy
    [JsonPropertyName("web_proxy")]
    public bool WebProxy { get; set; }
    [JsonPropertyName("webdav_policy")]
    public string WebdavPolicy { get; set; } = "native_proxy";
    [JsonPropertyName("proxy_range")]
    public bool ProxyRange { get; set; }
    [JsonPropertyName("down_proxy_url")]
    public string? DownProxyURL { get; set; }
    [JsonPropertyName("disable_proxy_sign")]
    public bool DisableProxySign { get; set; }

    [NotMapped]
    [JsonPropertyName("mount_details")]
    public StorageDetails? MountDetails { get; set; }

    public bool Webdav302() => WebdavPolicy == "302_redirect";
    public bool WebdavProxyURL() => WebdavPolicy == "use_proxy_url";
}

public class StorageDetails
{
    [JsonPropertyName("total_space")] public long TotalSpace { get; set; }
    [JsonPropertyName("used_space")] public long UsedSpace { get; set; }
    [JsonPropertyName("free_space")] public long FreeSpace => TotalSpace - UsedSpace;
}
