using OpenList.Core.Models;

namespace OpenList.Core.Drivers;

/// <summary>
/// Represents a file stream for upload operations.
/// </summary>
public class FileStreamer
{
    public Stream? Stream { get; set; }
    public IObj Obj { get; set; } = null!;
    public string Mimetype { get; set; } = string.Empty;
    public bool NeedStore { get; set; }
    public bool IsForceStreamUpload { get; set; }
    public IObj? Exist { get; set; }
    public long Size { get; set; }
}
