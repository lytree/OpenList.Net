namespace OpenList.Core.Drivers;

using System.Text.Json.Serialization;

public class DriverConfig
{
    public string Name { get; set; } = string.Empty;
    [JsonPropertyName("local_sort")] public bool LocalSort { get; set; }
    [JsonPropertyName("only_proxy")] public bool OnlyProxy { get; set; }
    [JsonPropertyName("no_cache")] public bool NoCache { get; set; }
    [JsonPropertyName("no_upload")] public bool NoUpload { get; set; }
    [JsonPropertyName("need_ms")] public bool NeedMs { get; set; }
    [JsonPropertyName("default_root")] public string DefaultRoot { get; set; } = string.Empty;
    [JsonPropertyName("check_status")] public bool CheckStatus { get; set; }
    public string? Alert { get; set; }
    [JsonPropertyName("no_overwrite_upload")] public bool NoOverwriteUpload { get; set; }
    [JsonPropertyName("proxy_range_option")] public bool ProxyRangeOption { get; set; }
    [JsonPropertyName("no_link_url")] public bool NoLinkURL { get; set; }
    [JsonPropertyName("link_cache_mode")] public LinkCacheMode LinkCacheMode { get; set; }
    [JsonPropertyName("only_indices")] public bool OnlyIndices { get; set; }
    [JsonPropertyName("prefer_proxy")] public bool PreferProxy { get; set; }

    public bool MustProxy() => OnlyProxy || NoLinkURL;
    public bool DefaultProxy() => PreferProxy;
}

public enum LinkCacheMode
{
    Auto = -1,
    None = 0,
    IP = 1 << 0,
    UA = 1 << 1
}

public class DriverItem
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Default { get; set; }
    public string? Options { get; set; }
    public bool Required { get; set; }
    public string? Help { get; set; }
}

public class DriverInfo
{
    public List<DriverItem> Common { get; set; } = [];
    public List<DriverItem> Additional { get; set; } = [];
    public DriverConfig Config { get; set; } = new();
}
