namespace OpenList.Core.Drivers;

public class DriverConfig
{
    public string Name { get; set; } = string.Empty;
    public bool LocalSort { get; set; }
    public bool OnlyProxy { get; set; }
    public bool NoCache { get; set; }
    public bool NoUpload { get; set; }
    public bool NeedMs { get; set; }
    public string DefaultRoot { get; set; } = string.Empty;
    public bool CheckStatus { get; set; }
    public string? Alert { get; set; }
    public bool NoOverwriteUpload { get; set; }
    public bool ProxyRangeOption { get; set; }
    public bool NoLinkURL { get; set; }
    public LinkCacheMode LinkCacheMode { get; set; }
    public bool OnlyIndices { get; set; }
    public bool PreferProxy { get; set; }

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
