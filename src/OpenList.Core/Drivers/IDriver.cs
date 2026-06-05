using OpenList.Core.Models;

namespace OpenList.Core.Drivers;

/// <summary>
/// Core driver interface - all storage drivers must implement this
/// </summary>
public interface IDriver : IDriverMeta, IDriverReader
{
}

public interface IDriverMeta
{
    DriverConfig Config();
    Storage GetStorage();
    void SetStorage(Storage storage);
    object GetAddition();
    Task InitAsync(CancellationToken ct = default);
    Task DropAsync(CancellationToken ct = default);
}

public interface IDriverReader
{
    Task<List<IObj>> ListAsync(IObj dir, ListArgs args, CancellationToken ct = default);
    Task<Link?> LinkAsync(IObj file, LinkArgs args, CancellationToken ct = default);
}

public interface IDriverGetRoot
{
    Task<IObj> GetRootAsync(CancellationToken ct = default);
}

public interface IDriverGetter
{
    Task<IObj?> GetAsync(string path, CancellationToken ct = default);
}

public interface IDriverMkdir
{
    Task MakeDirAsync(IObj parentDir, string dirName, CancellationToken ct = default);
}

public interface IDriverMkdirResult
{
    Task<IObj> MakeDirAsync(IObj parentDir, string dirName, CancellationToken ct = default);
}

public interface IDriverMove
{
    Task MoveAsync(IObj srcObj, IObj dstDir, CancellationToken ct = default);
}

public interface IDriverMoveResult
{
    Task<IObj> MoveAsync(IObj srcObj, IObj dstDir, CancellationToken ct = default);
}

public interface IDriverRename
{
    Task RenameAsync(IObj srcObj, string newName, CancellationToken ct = default);
}

public interface IDriverRenameResult
{
    Task<IObj> RenameAsync(IObj srcObj, string newName, CancellationToken ct = default);
}

public interface IDriverCopy
{
    Task CopyAsync(IObj srcObj, IObj dstDir, CancellationToken ct = default);
}

public interface IDriverCopyResult
{
    Task<IObj> CopyAsync(IObj srcObj, IObj dstDir, CancellationToken ct = default);
}

public interface IDriverRemove
{
    Task RemoveAsync(IObj obj, CancellationToken ct = default);
}

public interface IDriverPut
{
    Task PutAsync(IObj dstDir, FileStreamer file, Action<double> updateProgress, CancellationToken ct = default);
}

public interface IDriverPutResult
{
    Task<IObj> PutAsync(IObj dstDir, FileStreamer file, Action<double> updateProgress, CancellationToken ct = default);
}

public interface IDriverPutURL
{
    Task PutURLAsync(IObj dstDir, string name, string url, CancellationToken ct = default);
}

public interface IDriverOther
{
    Task<object?> OtherAsync(OtherArgs args, CancellationToken ct = default);
}

public interface IDriverArchiveReader
{
    Task<ArchiveMeta> GetArchiveMetaAsync(IObj obj, ArchiveArgs args, CancellationToken ct = default);
    Task<List<IObj>> ListArchiveAsync(IObj obj, ArchiveInnerArgs args, CancellationToken ct = default);
    Task<Link?> ExtractAsync(IObj obj, ArchiveInnerArgs args, CancellationToken ct = default);
}

public interface IDriverArchiveDecompress
{
    Task ArchiveDecompressAsync(IObj srcObj, IObj dstDir, ArchiveDecompressArgs args, CancellationToken ct = default);
}

public interface IDriverWithDetails
{
    Task<StorageDetails?> GetDetailsAsync(CancellationToken ct = default);
}

public interface IDriverDirectUploader
{
    List<string> GetDirectUploadTools();
    Task<object?> GetDirectUploadInfoAsync(string tool, IObj dstDir, string fileName, long fileSize, CancellationToken ct = default);
}

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

public class ArchiveMeta
{
    public bool IsEncrypted { get; set; }
    public List<ArchiveEntry> Entries { get; set; } = [];
}

public class ArchiveEntry
{
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public bool IsDir { get; set; }
    public DateTime Modified { get; set; }
    public string? InnerPath { get; set; }
}
