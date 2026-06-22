using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// File system operations: list, get, mkdir, rename, move, copy, remove, upload, archive.
/// Mirrors OpenList Go <c>server/handles/fsread.go</c> + <c>fsmanage.go</c> + <c>archive.go</c>.
/// </summary>
public interface IFileSystemService
{
    Task<Resp<FsListResp>> ListAsync(FsListReq req, User user, CancellationToken ct = default);
    Task<Resp<FsGetResp>> GetAsync(FsGetReq req, User user, CancellationToken ct = default);
    Task<Resp> MkdirAsync(string path, User user, CancellationToken ct = default);
    Task<Resp> RenameAsync(string oldPath, string newPath, User user, CancellationToken ct = default);
    Task<Resp> BatchRenameAsync(IReadOnlyList<BatchRenameItem> items, User user, CancellationToken ct = default);
    Task<Resp> RegexRenameAsync(string srcDir, string srcReg, string newReg, User user, CancellationToken ct = default);
    Task<Resp> MoveAsync(IReadOnlyList<string> srcPaths, string destDir, User user, CancellationToken ct = default);
    Task<Resp> RecursiveMoveAsync(string srcPath, string destDir, User user, CancellationToken ct = default);
    Task<Resp> CopyAsync(IReadOnlyList<string> srcPaths, string destDir, User user, CancellationToken ct = default);
    Task<Resp> RemoveAsync(IReadOnlyList<string> paths, User user, CancellationToken ct = default);
    Task<Resp> RemoveEmptyDirectoryAsync(string path, User user, CancellationToken ct = default);
    Task<Resp> PutStreamAsync(string path, Stream stream, User user, CancellationToken ct = default);
    Task<Resp> PutFormAsync(string path, Stream stream, string fileName, User user, CancellationToken ct = default);
    Task<Resp<DirectUploadInfo>> GetDirectUploadInfoAsync(string path, long size, User user, CancellationToken ct = default);
    Task<Resp<LinkResp>> LinkAsync(string path, User user, CancellationToken ct = default);
    Task<Resp> AddOfflineDownloadAsync(OfflineDownloadReq req, User user, CancellationToken ct = default);
    Task<Resp<ArchiveMetaResp>> ArchiveMetaAsync(string path, User user, CancellationToken ct = default);
    Task<Resp<ArchiveListResp>> ArchiveListAsync(string path, string? password, User user, CancellationToken ct = default);
    Task<Resp> ArchiveDecompressAsync(string archivePath, string destPath, string? password, User user, CancellationToken ct = default);
    Task<Resp<DirsResp>> DirsAsync(string path, User user, CancellationToken ct = default);
    Task<Resp> OtherAsync(string path, string action, User user, CancellationToken ct = default);
    Task<Resp<SearchResp>> SearchAsync(SearchReq req, User user, CancellationToken ct = default);
}

public sealed class FsListReq
{
    public string Path { get; set; } = "/";
    public string? Password { get; set; }
    public bool Refresh { get; set; }
    public int Page { get; set; } = 1;
    public int PerPage { get; set; } = 30;
}

public sealed class FsListResp
{
    public List<Obj> Content { get; set; } = [];
    public int Total { get; set; }
    public string? Readme { get; set; }
    public string? Header { get; set; }
    public bool Write { get; set; }
    public string? Provider { get; set; }
}

public sealed class FsGetReq
{
    public string Path { get; set; } = "/";
    public string? Password { get; set; }
}

public sealed class FsGetResp
{
    public Obj? Obj { get; set; }
    public List<Obj> Files { get; set; } = [];
    public bool IsDir { get; set; }
    public string? Readme { get; set; }
    public string? Header { get; set; }
    public bool Write { get; set; }
    public string? Provider { get; set; }
}

public sealed class BatchRenameItem
{
    public string SrcName { get; set; } = string.Empty;
    public string NewName { get; set; } = string.Empty;
}

public sealed class LinkResp
{
    public string Url { get; set; } = string.Empty;
    public string? Header { get; set; }
}

public sealed class OfflineDownloadReq
{
    public string Url { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string? Tool { get; set; }
}

public sealed class ArchiveMetaResp
{
    public string[] SupportedExtensions { get; set; } = [];
}

public sealed class ArchiveListResp
{
    public List<ArchiveEntry> Content { get; set; } = [];
    public int Total { get; set; }
}

public sealed class ArchiveEntry
{
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public bool IsDir { get; set; }
    public DateTimeOffset Modified { get; set; }
}

public sealed class DirsResp
{
    public List<string> Dirs { get; set; } = [];
}

public sealed class SearchReq
{
    public string Parent { get; set; } = "/";
    public string Keywords { get; set; } = string.Empty;
    public bool Recursive { get; set; } = true;
    public int Page { get; set; } = 1;
    public int PerPage { get; set; } = 30;
}

public sealed class SearchResp
{
    public List<Obj> Content { get; set; } = [];
    public int Total { get; set; }
}

public sealed class DirectUploadInfo
{
    public string Url { get; set; } = string.Empty;
    public Dictionary<string, string> Headers { get; set; } = new();
    public string? Method { get; set; }
}
