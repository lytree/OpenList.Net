namespace OpenList.Core.Models;

public class Link
{
    public string? URL { get; set; }
    public Dictionary<string, string>? Header { get; set; }
    public TimeSpan? Expiration { get; set; }
    public int Concurrency { get; set; }
    public int PartSize { get; set; }
    public long ContentLength { get; set; }
}

public class ListArgs
{
    public string ReqPath { get; set; } = string.Empty;
    public bool S3ShowPlaceholder { get; set; }
    public bool Refresh { get; set; }
    public bool WithStorageDetails { get; set; }
    public bool SkipHook { get; set; }
}

public class LinkArgs
{
    public string IP { get; set; } = string.Empty;
    public Dictionary<string, string>? Header { get; set; }
    public string Type { get; set; } = string.Empty;
    public bool Redirect { get; set; }
}

public class OtherArgs
{
    public IObj Obj { get; set; } = null!;
    public string Method { get; set; } = string.Empty;
    public object? Data { get; set; }
}

public class ArchiveArgs
{
    public string Password { get; set; } = string.Empty;
    public LinkArgs LinkArgs { get; set; } = new();
}

public class ArchiveInnerArgs : ArchiveArgs
{
    public string InnerPath { get; set; } = string.Empty;
}

public class ArchiveDecompressArgs : ArchiveInnerArgs
{
    public bool CacheFull { get; set; }
    public bool PutIntoNewDir { get; set; }
    public bool Overwrite { get; set; }
}

public class WrongArchivePasswordException : Exception
{
    public WrongArchivePasswordException() : base("wrong archive password") { }
    public WrongArchivePasswordException(string message) : base(message) { }
}

public class ArchiveListArgs
{
    public ArchiveInnerArgs ArchiveInnerArgs { get; set; } = new();
    public bool Refresh { get; set; }
    public int Page { get; set; } = 1;
    public int PerPage { get; set; } = int.MaxValue;
}

public class ArchiveMetaResult
{
    public string Comment { get; set; } = "";
    public bool IsEncrypted { get; set; }
    public List<ArchiveContentEntry> Content { get; set; } = [];
    /// <summary>Go: ret.DriverProviding → determines /ad vs /ae for RawURL</summary>
    public bool DriverProviding { get; set; }
}

public class ArchiveContentEntry
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public bool IsDir { get; set; }
    public DateTime Modified { get; set; }
    public DateTime Created { get; set; }
    public int Type { get; set; }
    public List<ArchiveContentEntry> Children { get; set; } = [];
}

public class PageReq
{
    public int Page { get; set; } = 1;
    public int PerPage { get; set; } = int.MaxValue;

    public void Validate()
    {
        if (Page < 1) Page = 1;
        if (PerPage < 1) PerPage = int.MaxValue;
    }
}
