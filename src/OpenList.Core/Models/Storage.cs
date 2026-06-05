namespace OpenList.Core.Models;

public class Storage
{
    public uint Id { get; set; }
    public string MountPath { get; set; } = string.Empty;
    public int Order { get; set; }
    public string Driver { get; set; } = string.Empty;
    public int CacheExpiration { get; set; }
    public string? CustomCachePolicies { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Addition { get; set; } = "{}";
    public string? Remark { get; set; }
    public DateTime Modified { get; set; } = DateTime.UtcNow;
    public bool Disabled { get; set; }
    public bool DisableIndex { get; set; }
    public bool EnableSign { get; set; }

    // Sort
    public string? OrderBy { get; set; }
    public string? OrderDirection { get; set; }
    public string? ExtractFolder { get; set; }

    // Proxy
    public bool WebProxy { get; set; }
    public string WebdavPolicy { get; set; } = "native_proxy";
    public bool ProxyRange { get; set; }
    public string? DownProxyURL { get; set; }
    public bool DisableProxySign { get; set; }

    public bool Webdav302() => WebdavPolicy == "302_redirect";
    public bool WebdavProxyURL() => WebdavPolicy == "use_proxy_url";
}

public class DiskUsage
{
    public long TotalSpace { get; set; }
    public long UsedSpace { get; set; }
    public long FreeSpace => TotalSpace - UsedSpace;
}

public class StorageDetails
{
    public DiskUsage DiskUsage { get; set; } = new();
}
