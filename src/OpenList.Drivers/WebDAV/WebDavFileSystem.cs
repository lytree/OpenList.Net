using System.Net;
using System.Text;
using WebDav;
using Zio;
using Zio.FileSystems;

namespace OpenList.Drivers.WebDAV;

/// <summary>
/// Zio FileSystem implementation backed by a WebDAV server.
/// </summary>
internal sealed class WebDavFileSystem : NetworkFileSystemBase
{
    private readonly WebDavClient _client;
    private readonly string _baseUrl;
    private readonly string _rootPath;

    public WebDavFileSystem(WebDavClient client, string baseUrl, string rootPath)
    {
        _client = client;
        _baseUrl = baseUrl.TrimEnd('/');
        _rootPath = string.IsNullOrEmpty(rootPath) ? "" : rootPath.Trim('/');
    }

    // --- Path conversion ---

    private string ToUrl(UPath path)
    {
        var relative = path.FullName.TrimStart('/');
        var full = string.IsNullOrEmpty(_rootPath)
            ? relative
            : _rootPath + "/" + relative;
        return string.IsNullOrEmpty(full) ? _baseUrl + "/" : _baseUrl + "/" + full;
    }

    private static string GetRelativeName(string uri, string parentUrl)
    {
        var name = uri.TrimEnd('/');
        var idx = name.LastIndexOf('/');
        return idx >= 0 ? name[(idx + 1)..] : name;
    }

    // --- Existence checks ---

    protected override bool DirectoryExistsImpl(UPath path)
    {
        var resp = _client.Propfind(ToUrl(path), new PropfindParameters
        {
            RequestType = PropfindRequestType.NamedProperties,
        }).GetAwaiter().GetResult();
        return resp.IsSuccessful && resp.Resources.Any(r => r.IsCollection);
    }

    protected override bool FileExistsImpl(UPath path)
    {
        var resp = _client.Propfind(ToUrl(path)).GetAwaiter().GetResult();
        return resp.IsSuccessful && resp.Resources.Any(r => !r.IsCollection);
    }

    // --- Directory operations ---

    protected override void CreateDirectoryImpl(UPath path)
    {
        var resp = _client.Mkcol(ToUrl(path)).GetAwaiter().GetResult();
        if (!resp.IsSuccessful && resp.StatusCode != 405) // 405 = already exists
            throw new IOException($"Failed to create directory {path}: {resp.Description}");
    }

    protected override void DeleteDirectoryImpl(UPath path, bool isRecursive)
    {
        if (isRecursive)
        {
            foreach (var child in EnumeratePaths(path, "*", SearchOption.AllDirectories, SearchTarget.Both))
            {
                _client.Delete(ToUrl(child)).GetAwaiter().GetResult();
            }
        }
        _client.Delete(ToUrl(path)).GetAwaiter().GetResult();
    }

    // --- File operations ---

    protected override void DeleteFileImpl(UPath path)
    {
        _client.Delete(ToUrl(path)).GetAwaiter().GetResult();
    }

    protected override void MoveFileImpl(UPath srcPath, UPath destPath)
    {
        _client.Move(ToUrl(srcPath), ToUrl(destPath)).GetAwaiter().GetResult();
    }

    // --- File open ---

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

        // Write modes: buffer in memory, upload on dispose
        return new WebDavWriteStream(_client, url, mode);
    }

    // --- Metadata ---

    protected override long GetFileLengthImpl(UPath path)
    {
        var resp = _client.Propfind(ToUrl(path)).GetAwaiter().GetResult();
        var res = resp.Resources.FirstOrDefault(r => !r.IsCollection);
        return res?.ContentLength ?? 0;
    }

    protected override DateTime GetLastWriteTimeImpl(UPath path)
    {
        var resp = _client.Propfind(ToUrl(path)).GetAwaiter().GetResult();
        var res = resp.Resources.FirstOrDefault();
        return res?.LastModifiedDate ?? DateTime.MinValue;
    }

    protected override DateTime GetCreationTimeImpl(UPath path)
    {
        var resp = _client.Propfind(ToUrl(path)).GetAwaiter().GetResult();
        var res = resp.Resources.FirstOrDefault();
        return res?.CreationDate ?? DateTime.MinValue;
    }

    // --- Enumeration ---

    protected override IEnumerable<UPath> EnumeratePathsImpl(UPath path, string searchPattern, SearchOption searchOption, SearchTarget searchTarget)
    {
        var url = ToUrl(path);
        var resp = _client.Propfind(url, new PropfindParameters
        {
            RequestType = PropfindRequestType.AllProperties,
        }).GetAwaiter().GetResult();

        if (!resp.IsSuccessful) yield break;

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
        // Simple glob: * matches any, ? matches single
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    // --- Write stream that uploads on dispose ---

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

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_uploaded)
            {
                _uploaded = true;
                Position = 0;
                _client.PutFile(_url, this).GetAwaiter().GetResult();
            }
            base.Dispose(disposing);
        }
    }
}
