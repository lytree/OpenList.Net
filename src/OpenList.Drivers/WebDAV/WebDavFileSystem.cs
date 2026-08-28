using System.Net;
using System.Text;
using WebDav;
using Zio;
using Zio.FileSystems;

namespace OpenList.Drivers.WebDAV;

/// <summary>
/// 基于 WebDAV 服务器的 Zio 文件系统实现。
/// </summary>
internal sealed class WebDavFileSystem : NetworkFileSystemBase
{
    private readonly WebDavClient _client;
    private readonly string _baseUrl;
    private readonly string _rootPath;

    /// <summary>构造：规范化基础 URL（去末尾斜杠）与根路径。</summary>
    public WebDavFileSystem(WebDavClient client, string baseUrl, string rootPath)
    {
        _client = client;
        _baseUrl = baseUrl.TrimEnd('/');
        _rootPath = string.IsNullOrEmpty(rootPath) ? "" : rootPath.Trim('/');
    }

    // --- 路径转换 ---

    /// <summary>把 Zio 路径映射为 WebDAV 请求用的完整 URL。</summary>
    private string ToUrl(UPath path)
    {
        var relative = path.FullName.TrimStart('/');
        var full = string.IsNullOrEmpty(_rootPath)
            ? relative
            : _rootPath + "/" + relative;
        return string.IsNullOrEmpty(full) ? _baseUrl + "/" : _baseUrl + "/" + full;
    }

    /// <summary>从资源 URI 中截取相对父级 URL 的最终名称。</summary>
    private static string GetRelativeName(string uri, string parentUrl)
    {
        var name = uri.TrimEnd('/');
        var idx = name.LastIndexOf('/');
        return idx >= 0 ? name[(idx + 1)..] : name;
    }

    // --- 存在性检查 ---

    /// <summary>通过 PROPFIND 判断目录是否存在（存在类型为集合的资源）。</summary>
    protected override bool DirectoryExistsImpl(UPath path)
    {
        var resp = _client.Propfind(ToUrl(path), new PropfindParameters
        {
            RequestType = PropfindRequestType.NamedProperties,
        }).GetAwaiter().GetResult();
        return resp.IsSuccessful && resp.Resources.Any(r => r.IsCollection);
    }

    /// <summary>通过 PROPFIND 判断文件是否存在（存在类型非集合的资源）。</summary>
    protected override bool FileExistsImpl(UPath path)
    {
        var resp = _client.Propfind(ToUrl(path)).GetAwaiter().GetResult();
        return resp.IsSuccessful && resp.Resources.Any(r => !r.IsCollection);
    }

    // --- 目录操作 ---

    /// <summary>创建目录 = MKCOL 请求（405 表示已存在，视为成功）。</summary>
    protected override void CreateDirectoryImpl(UPath path)
    {
        var resp = _client.Mkcol(ToUrl(path)).GetAwaiter().GetResult();
        if (!resp.IsSuccessful && resp.StatusCode != 405) // 405 = already exists
            throw new IOException($"Failed to create directory {path}: {resp.Description}");
    }

    /// <summary>递归删除目录：先删除全部子项再删除目录本身。</summary>
    protected override void DeleteDirectoryImpl(UPath path, bool isRecursive)
    {
        if (isRecursive)
        {
            // 逐一枚举并删除目录树内的所有子资源。
            foreach (var child in EnumeratePaths(path, "*", SearchOption.AllDirectories, SearchTarget.Both))
            {
                _client.Delete(ToUrl(child)).GetAwaiter().GetResult();
            }
        }
        _client.Delete(ToUrl(path)).GetAwaiter().GetResult();
    }

    // --- 文件操作 ---

    /// <summary>删除文件 = DELETE 请求。</summary>
    protected override void DeleteFileImpl(UPath path)
    {
        _client.Delete(ToUrl(path)).GetAwaiter().GetResult();
    }

    /// <summary>移动文件 = MOVE 请求。</summary>
    protected override void MoveFileImpl(UPath srcPath, UPath destPath)
    {
        _client.Move(ToUrl(srcPath), ToUrl(destPath)).GetAwaiter().GetResult();
    }

    // --- 打开文件 ---

    /// <summary>
    /// 打开文件：读模式拉取原始响应流；写模式使用释放时上传的 <see cref="WebDavWriteStream"/>。
    /// </summary>
    protected override Stream OpenFileImpl(UPath path, FileMode mode, FileAccess access, FileShare share)
    {
        var url = ToUrl(path);

        if (access == FileAccess.Read || access == FileAccess.ReadWrite && mode == FileMode.Open)
        {
            var resp = _client.GetRawFile(url).GetAwaiter().GetResult();
            if (!resp.IsSuccessful)
                throw new FileNotFoundException($"File not found: {path}");
            return resp.Stream;
        }

        // 写模式：先在内存缓冲，Dispose 时统一 PUT 上传。
        return new WebDavWriteStream(_client, url, mode);
    }

    // --- 元数据 ---

    /// <summary>文件长度 = 首个非集合资源的 ContentLength。</summary>
    protected override long GetFileLengthImpl(UPath path)
    {
        var resp = _client.Propfind(ToUrl(path)).GetAwaiter().GetResult();
        var res = resp.Resources.FirstOrDefault(r => !r.IsCollection);
        return res?.ContentLength ?? 0;
    }

    /// <summary>最后写入时间 = 资源的 LastModifiedDate。</summary>
    protected override DateTime GetLastWriteTimeImpl(UPath path)
    {
        var resp = _client.Propfind(ToUrl(path)).GetAwaiter().GetResult();
        var res = resp.Resources.FirstOrDefault();
        return res?.LastModifiedDate ?? DateTime.MinValue;
    }

    /// <summary>创建时间 = 资源的 CreationDate。</summary>
    protected override DateTime GetCreationTimeImpl(UPath path)
    {
        var resp = _client.Propfind(ToUrl(path)).GetAwaiter().GetResult();
        var res = resp.Resources.FirstOrDefault();
        return res?.CreationDate ?? DateTime.MinValue;
    }

    // --- 枚举 ---

    /// <summary>枚举路径：PROPFIND 拉取子资源列表并按搜索目标/通配符过滤，必要时递归。</summary>
    protected override IEnumerable<UPath> EnumeratePathsImpl(UPath path, string searchPattern, SearchOption searchOption, SearchTarget searchTarget)
    {
        var url = ToUrl(path);
        var resp = _client.Propfind(url, new PropfindParameters
        {
            RequestType = PropfindRequestType.AllProperties,
        }).GetAwaiter().GetResult();

        if (!resp.IsSuccessful) yield break;

        // 首次 PROPFIND 不会返回目录自身，但仍过滤掉与当前 URL 相同的项以防重复。
        var resources = resp.Resources
            .Where(r => !string.Equals(r.Uri, url, StringComparison.OrdinalIgnoreCase) &&
                        !r.Uri.EndsWith(url + "/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var res in resources)
        {
            var name = GetRelativeName(res.Uri, url);
            if (string.IsNullOrEmpty(name)) continue;
            var childPath = path / name;
            var isDir = res.IsCollection;

            if ((searchTarget == SearchTarget.Both) ||
                (searchTarget == SearchTarget.Directory && isDir) ||
                (searchTarget == SearchTarget.File && !isDir))
            {
                if (MatchesPattern(name, searchPattern))
                    yield return childPath;
            }

            // 需递归时对有集合类型的子项逐层下钻。
            if (searchOption == SearchOption.AllDirectories && isDir)
            {
                foreach (var sub in EnumeratePathsImpl(childPath, searchPattern, searchOption, searchTarget))
                    yield return sub;
            }
        }
    }

    private static bool MatchesPattern(string name, string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*") return true;
        // 简单通配：* 匹配任意多个字符，? 匹配单个字符。
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    // --- 释放时上传的写流 ---

    /// <summary>仅写流：内容缓存在内存，Dispose 时通过 PUT 上传到 WebDAV。</summary>
    private sealed class WebDavWriteStream : MemoryStream
    {
        private readonly WebDavClient _client;
        private readonly string _url;
        private readonly FileMode _mode;
        private bool _uploaded;

        public WebDavWriteStream(WebDavClient client, string url, FileMode mode)
        {
            _client = client;
            _url = url;
            _mode = mode;
        }

        /// <summary>释放时把缓冲内容 PUT 到目标 URL（仅执行一次）。</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_uploaded)
            {
                _uploaded = true;
                Position = 0;
                // 将整段缓冲作为文件内容上传。
                _client.PutFile(_url, this).GetAwaiter().GetResult();
            }
            base.Dispose(disposing);
        }
    }
}
