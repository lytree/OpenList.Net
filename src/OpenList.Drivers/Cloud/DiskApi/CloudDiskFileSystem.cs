using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Vfs;

namespace OpenList.Drivers.Cloud.DiskApi;

/// <summary>
/// 云盘节点（文件或目录）的统一表示。各云盘驱动把自家 JSON 映射成它，
/// 由 <see cref="CloudDiskFileSystem"/> 统一做路径解析与 VFS 适配。
/// </summary>
public sealed class CloudDiskNode
{
    /// <summary>云端对象 id。</summary>
    public string Id { get; init; } = string.Empty;
    /// <summary>父目录 id。</summary>
    public string ParentId { get; init; } = string.Empty;
    /// <summary>名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>是否为目录。</summary>
    public bool IsFolder { get; init; }
    /// <summary>大小（字节）。</summary>
    public long Size { get; init; }
    /// <summary>修改时间。</summary>
    public DateTimeOffset Modified { get; init; }
    /// <summary>创建时间。</summary>
    public DateTimeOffset Created { get; init; }
}

/// <summary>
/// 云盘 API 的最小契约：只要实现这 7 个操作，即可复用
/// <see cref="CloudDiskFileSystem"/> 的完整 VFS 视图（列举/解析/读写/增删改）。
/// </summary>
public interface ICloudDiskApi
{
    /// <summary>挂载根目录 id。</summary>
    string RootId { get; }
    /// <summary>用于拉取直链的 HTTP 客户端。</summary>
    HttpClient HttpClient { get; }
    /// <summary>列出目录下的全部子项（驱动内部负责翻页）。</summary>
    Task<IReadOnlyList<CloudDiskNode>> ListAsync(string parentId, CancellationToken ct);
    /// <summary>按 id 取单个节点，不存在返回 null。</summary>
    Task<CloudDiskNode?> GetAsync(string id, CancellationToken ct);
    /// <summary>取下载直链，失败返回 null。</summary>
    Task<string?> GetDownloadUrlAsync(string id, CancellationToken ct);
    /// <summary>在父目录下创建文件夹。</summary>
    Task<CloudDiskNode> MakeDirAsync(string parentId, string name, CancellationToken ct);
    /// <summary>批量删除。</summary>
    Task DeleteAsync(IReadOnlyList<string> ids, CancellationToken ct);
    /// <summary>移动/改名（newParentId 为空表示不改父目录，newName 为空表示不改名）。</summary>
    Task MoveAsync(string id, string? newParentId, string? newName, CancellationToken ct);
}

/// <summary>
/// 云盘驱动的通用 VFS 视图：把"按 id 寻址"的云盘 API 包装成按路径访问的文件系统。
/// 解析策略与 <c>AliyundriveOpenFileSystem</c> 一致——从根 id 出发逐级列目录下钻，
/// 命中结果写入缓存，写操作后按前缀失效。
/// </summary>
internal sealed class CloudDiskFileSystem : NetworkFileSystemBase
{
    private readonly ICloudDiskApi _api;

    /// <summary>路径 → 云端节点缓存（键为规范化绝对路径）。</summary>
    private readonly ConcurrentDictionary<string, CloudDiskNode> _cache = new(StringComparer.Ordinal);

    internal CloudDiskFileSystem(ICloudDiskApi api) => _api = api;

    /// <summary>把 VFS 路径规范化为缓存键。</summary>
    private static string Key(UPath path)
    {
        var full = path.FullName;
        if (string.IsNullOrEmpty(full)) return "/";
        return full.Length > 1 ? full.TrimEnd('/') : full;
    }

    /// <summary>路径 → 云端节点；根路径返回 null（由配置的根 id 表示）。</summary>
    internal async Task<CloudDiskNode?> ResolveAsync(UPath path, CancellationToken ct = default)
    {
        var key = Key(path);
        if (key == "/") return null;
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var segments = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parentId = _api.RootId;
        var walked = string.Empty;
        CloudDiskNode? current = null;

        foreach (var segment in segments)
        {
            var children = await _api.ListAsync(parentId, ct).ConfigureAwait(false);
            var match = children.FirstOrDefault(f => string.Equals(f.Name, segment, StringComparison.Ordinal));
            if (match is null) return null;

            walked += "/" + segment;
            _cache[walked] = match;
            current = match;
            parentId = match.Id;
        }
        return current;
    }

    /// <summary>让路径及其子路径的缓存失效（写操作后调用）。</summary>
    internal void InvalidateDirectory(UPath path)
    {
        var key = Key(path);
        foreach (var cached in _cache.Keys)
        {
            if (cached.Equals(key, StringComparison.Ordinal) || cached.StartsWith(key + "/", StringComparison.Ordinal))
                _cache.TryRemove(cached, out _);
        }
    }

    protected override bool DirectoryExistsImpl(UPath path)
        => Key(path) == "/" || ResolveAsync(path).GetAwaiter().GetResult() is { IsFolder: true };

    protected override bool FileExistsImpl(UPath path)
        => ResolveAsync(path).GetAwaiter().GetResult() is { IsFolder: false };

    protected override void CreateDirectoryImpl(UPath path)
    {
        var parent = ResolveAsync(path.GetDirectory()).GetAwaiter().GetResult();
        var created = _api.MakeDirAsync(parent?.Id ?? _api.RootId, path.GetName(), CancellationToken.None)
            .GetAwaiter().GetResult();
        _cache[Key(path)] = created;
    }

    protected override void DeleteDirectoryImpl(UPath path, bool isRecursive) => DeleteEntry(path);

    protected override void DeleteFileImpl(UPath path) => DeleteEntry(path);

    private void DeleteEntry(UPath path)
    {
        var node = ResolveAsync(path).GetAwaiter().GetResult()
            ?? throw new FileNotFoundException($"Path not found: {path}");
        _api.DeleteAsync([node.Id], CancellationToken.None).GetAwaiter().GetResult();
        InvalidateDirectory(path);
    }

    protected override void MoveFileImpl(UPath srcPath, UPath destPath) => MoveEntry(srcPath, destPath);

    protected override void MoveDirectoryImpl(UPath srcPath, UPath destPath) => MoveEntry(srcPath, destPath);

    private void MoveEntry(UPath srcPath, UPath destPath)
    {
        var src = ResolveAsync(srcPath).GetAwaiter().GetResult()
            ?? throw new FileNotFoundException($"Path not found: {srcPath}");
        var destParent = ResolveAsync(destPath.GetDirectory()).GetAwaiter().GetResult();
        _api.MoveAsync(src.Id, destParent?.Id ?? _api.RootId, destPath.GetName(), CancellationToken.None)
            .GetAwaiter().GetResult();
        InvalidateDirectory(srcPath);
        InvalidateDirectory(destPath);
    }

    /// <summary>云盘侧复制：这些驱动不提供原生 copy，显式拒绝以免误走"边读边写"的慢路径。</summary>
    protected override void CopyFileImpl(UPath srcPath, UPath destPath, bool overwrite)
        => throw new NotSupportedException("Cloud disk driver does not support server-side copy; download then upload instead.");

    protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
    {
        if (access is FileAccess.Write or FileAccess.ReadWrite)
            throw new NotSupportedException("Cloud disk driver does not support stream writes.");

        var node = ResolveAsync(path, CancellationToken.None).GetAwaiter().GetResult()
            ?? throw new FileNotFoundException($"Path not found: {path}");
        if (node.IsFolder) throw new InvalidOperationException($"Cannot open a directory as a file: {path}");

        var url = _api.GetDownloadUrlAsync(node.Id, CancellationToken.None).GetAwaiter().GetResult()
            ?? throw new IOException($"Failed to resolve download url: {path}");

        var response = _api.HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new IOException($"Download failed with status {(int)response.StatusCode}: {path}");
        }
        var content = response.Content.ReadAsStream();
        return new HttpContentStream(response, content);
    }

    protected override long GetFileLengthImpl(UPath path)
        => ResolveAsync(path).GetAwaiter().GetResult()?.Size ?? 0;

    protected override DateTime GetLastWriteTimeImpl(UPath path)
        => (ResolveAsync(path).GetAwaiter().GetResult()?.Modified ?? DateTimeOffset.MinValue).LocalDateTime;

    protected override DateTime GetCreationTimeImpl(UPath path)
        => (ResolveAsync(path).GetAwaiter().GetResult()?.Created ?? DateTimeOffset.MinValue).LocalDateTime;

    protected override IEnumerable<UPath> EnumeratePathsImpl(
        UPath path, string searchPattern, SearchDepth searchOption, SearchTarget searchTarget)
    {
        var dir = ResolveAsync(path).GetAwaiter().GetResult();
        var folderId = dir?.Id ?? _api.RootId;
        var children = _api.ListAsync(folderId, CancellationToken.None).GetAwaiter().GetResult();

        foreach (var child in children)
        {
            if (string.IsNullOrEmpty(child.Name) || !MatchesPattern(child.Name, searchPattern)) continue;

            var childPath = path / child.Name;
            _cache[Key(childPath)] = child;

            if (searchTarget is SearchTarget.Both
                || (searchTarget == SearchTarget.Directory && child.IsFolder)
                || (searchTarget == SearchTarget.File && !child.IsFolder))
            {
                yield return childPath;
            }

            if (searchOption == SearchDepth.AllDirectories && child.IsFolder)
            {
                foreach (var sub in EnumeratePathsImpl(childPath, searchPattern, searchOption, searchTarget))
                    yield return sub;
            }
        }
    }

    private static bool MatchesPattern(string name, string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*") return true;
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
    }
}

/// <summary>
/// 云盘驱动公共基类：把"路径归一化 → 解析节点 → 取直链"这套重复逻辑收在这里，
/// 子类只需提供 <see cref="CreateApi"/> 与配置字段。
/// </summary>
public abstract class CloudDiskDriverBase : CloudDriverBase, IFileSystemLinkProvider
{
    private CloudDiskFileSystem? _fs;

    /// <summary>驱动专属的云盘 API 客户端（初始化时创建一次，保持登录态）。</summary>
    protected ICloudDiskApi Api { get; private set; } = null!;

    /// <summary>创建驱动专属的云盘 API 客户端。</summary>
    protected abstract ICloudDiskApi CreateApi();

    /// <summary>初始化后挂载云盘文件系统（子类在 <c>InitAsync</c> 末尾调用）。</summary>
    protected void AttachFileSystem()
    {
        Api = CreateApi();
        _fs = new CloudDiskFileSystem(Api);
        System = _fs;
    }

    /// <summary>挂载路径归一化为驱动相对路径。</summary>
    protected string ToRelative(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        if (!string.IsNullOrEmpty(MountPath) && path.StartsWith(MountPath, StringComparison.OrdinalIgnoreCase))
        {
            var rest = path[MountPath.Length..];
            return string.IsNullOrEmpty(rest) ? "/" : (rest.StartsWith('/') ? rest : "/" + rest);
        }
        return path.StartsWith('/') ? path : "/" + path;
    }

    /// <summary>解析路径对应的云端节点（供子类读取 id 使用）。</summary>
    protected async Task<CloudDiskNode?> ResolveNodeAsync(string path, CancellationToken ct)
    {
        var fs = _fs ?? throw new InvalidOperationException($"{DriverName} driver is not initialized");
        return await fs.ResolveAsync(new UPath(ToRelative(path)), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Uri?> GetLinkAsync(string path, CancellationToken ct = default)
    {
        var node = await ResolveNodeAsync(path, ct).ConfigureAwait(false);
        if (node is null || node.IsFolder) return null;
        var url = await DownloadUrlForNodeAsync(node, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(url) ? null : new Uri(url);
    }

    /// <inheritdoc />
    public async Task<FileLink?> GetFileLinkAsync(string path, bool proxy, CancellationToken ct = default)
    {
        var node = await ResolveNodeAsync(path, ct).ConfigureAwait(false);
        if (node is null || node.IsFolder) return null;
        var url = await DownloadUrlForNodeAsync(node, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(url)) return null;
        // 云盘直链可直接 302；代理模式交给通用 VFS 流式传输（OpenFile 内部同样走直链）。
        return proxy ? null : new FileLink { Url = url };
    }

    /// <summary>取节点直链（子类可覆写以补充请求头/签名）。</summary>
    private async Task<string?> DownloadUrlForNodeAsync(CloudDiskNode node, CancellationToken ct)
        => await Api.GetDownloadUrlAsync(node.Id, ct).ConfigureAwait(false);
}
