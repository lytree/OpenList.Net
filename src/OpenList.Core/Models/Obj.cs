using Zio;

namespace OpenList.Core.Models;

/// <summary>
/// Unified file/directory object returned by drivers and endpoints.
/// Mirrors OpenList Go <c>model.Obj</c>.
/// </summary>
public class Obj
{
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTimeOffset Modified { get; set; }
    public DateTimeOffset Created { get; set; }
    public bool IsDir { get; set; }
    public string? Sign { get; set; }
    public string? Thumb { get; set; }
    public int Type { get; set; }
    public string? HashInfo { get; set; }
    public HashInfo? HashInfoObj { get; set; }
}

public sealed class HashInfo
{
    public string? Md5 { get; set; }
    public string? Sha1 { get; set; }
    public string? Sha256 { get; set; }

    public string ToHashInfoString() => Md5 is not null ? $"md5:{Md5}" : Sha1 is not null ? $"sha1:{Sha1}" : Sha256 is not null ? $"sha256:{Sha256}" : string.Empty;
}

public sealed class ObjPath
{
    public UPath Path { get; set; }
    public string Name => Path.GetName();
    public bool IsRoot => Path == UPath.Root;
}
