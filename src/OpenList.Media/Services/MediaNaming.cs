using System.Text.RegularExpressions;
using OpenList.Media.Domain;

namespace OpenList.Media.Services;

/// <summary>
/// 扫描期的命名规则（纯函数，无 IO、无数据库）：
/// 哪些文件算视频、视频归属到哪个条目目录、条目类型如何判定、标题与年份怎么解析。
/// <para>
/// 从 <c>MediaScanService</c> 里抽出来的原因：这些规则是媒体域里最容易被追问、
/// 也最需要回归保护的部分（"Blade Runner 2049 (2017)" 这类片名带数字的情况就踩过坑），
/// 单独成类后才能被单元测试直接覆盖（见 tests/OpenList.Tests/MediaNamingTests.cs）。
/// 后续接 NFO / 番号识别时，新规则也应加在这里，而不是散回扫描流程。
/// </para>
/// </summary>
internal static partial class MediaNaming
{
    /// <summary>识别为视频的扩展名集合。</summary>
    internal static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts",
        ".m2ts", ".mpg", ".mpeg", ".rmvb", ".rm", ".3gp", ".vob", ".iso", ".strm",
    };

    /// <summary>文件名是否属于视频（按扩展名判断，忽略大小写）。</summary>
    internal static bool IsVideoFile(string fileName)
        => VideoExtensions.Contains(Path.GetExtension(fileName));

    // ------------------------------------------------------------------
    // Sidecar（随片元数据）识别与挑选：NFO / 海报 / 背景图。
    // 扫描器只负责把「目录里有什么文件」递进来，挑选规则集中在这里保持可测。
    // ------------------------------------------------------------------

    /// <summary>海报候选的文件基名（不含扩展名，忽略大小写）。</summary>
    private static readonly IReadOnlySet<string> PosterBaseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "poster", "folder", "cover" };

    /// <summary>背景图候选的文件基名（忽略大小写）。</summary>
    private static readonly IReadOnlySet<string> BackdropBaseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "fanart", "backdrop", "background" };

    /// <summary>文件是否为 NFO sidecar。</summary>
    internal static bool IsNfoFile(string fileName)
        => string.Equals(Path.GetExtension(fileName), ".nfo", StringComparison.OrdinalIgnoreCase);

    /// <summary>文件是否为海报或背景图 sidecar；返回 null 表示都不是。
    /// 接受精确基名（poster/fanart）与带条目前缀的变体（&lt;条目名&gt;-poster.jpg，TinyMediaManager 约定）。</summary>
    internal static string? ClassifyImageFile(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (!ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".webp", StringComparison.OrdinalIgnoreCase))
            return null;
        var baseName = FileNameWithoutExt(fileName);
        // 变体形如 "<条目名>-poster" / "-fanart"（TinyMediaManager 约定），后缀直接定类。
        foreach (var suffix in new[] { "-poster", "-cover" })
            if (baseName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return "poster";
        foreach (var suffix in new[] { "-fanart", "-backdrop", "-background" })
            if (baseName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return "backdrop";
        if (PosterBaseNames.Contains(baseName)) return "poster";
        if (BackdropBaseNames.Contains(baseName)) return "backdrop";
        return null;
    }

    /// <summary>
    /// 条目 sidecar（NFO/图片）所在的目录：目录条目就是条目目录本身；
    /// 散文件条目（条目键是文件路径）是其父目录。
    /// </summary>
    internal static string SidecarDirectory(string itemDir, string rootPath)
        => IsVideoFile(BaseName(itemDir)) ? DirectoryOf(itemDir) : itemDir;

    /// <summary>
    /// 从一组 NFO 路径里为条目挑选最合适的一个（只看 sidecar 目录内的）：
    /// 优先 &lt;条目名&gt;.nfo（Kodi 约定，散文件条目也靠它命中 Movie.mkv ↔ Movie.nfo），
    /// 其次 movie.nfo / tvshow.nfo（Jellyfin 约定），最后该目录里唯一的 NFO；
    /// 多于一个且都无法按约定区分时放弃（宁可不用也不猜错）。
    /// </summary>
    internal static string? SelectNfoPath(string itemDir, string rootPath, IEnumerable<string> nfoPaths)
    {
        var itemName = BaseName(itemDir);
        var ext = Path.GetExtension(itemName);
        if (!string.IsNullOrEmpty(ext) && IsVideoFile(itemName)) itemName = itemName[..^ext.Length];
        var sidecarDir = SidecarDirectory(itemDir, rootPath);
        var inDir = nfoPaths.Where(p => DirectoryOf(p) == sidecarDir).ToList();

        var byItemName = inDir.FirstOrDefault(p => FileNameWithoutExt(p).Equals(itemName, StringComparison.OrdinalIgnoreCase));
        if (byItemName is not null) return byItemName;

        var byConvention = inDir.FirstOrDefault(p =>
        {
            var b = FileNameWithoutExt(p);
            return b.Equals("movie", StringComparison.OrdinalIgnoreCase)
                || b.Equals("tvshow", StringComparison.OrdinalIgnoreCase);
        });
        if (byConvention is not null) return byConvention;

        return inDir.Count == 1 ? inDir[0] : null;
    }

    /// <summary>
    /// 从图片 sidecar 里挑选海报/背景图路径（只看 sidecar 目录内的）。
    /// 优先精确基名（poster.jpg），其次带条目名的变体
    /// （&lt;条目名&gt;-poster.jpg，TinyMediaManager 约定）；
    /// 同类多个且无法区分时取文件名字典序最小的一个，保证行为确定可测。
    /// </summary>
    /// <returns>0 号元素为海报路径，1 号为背景图路径；未找到的位置为 null。</returns>
    internal static (string? Poster, string? Backdrop) SelectImagePaths(
        string itemDir, string rootPath, IEnumerable<string> imagePaths)
    {
        var itemName = BaseName(itemDir);
        var ext = Path.GetExtension(itemName);
        if (!string.IsNullOrEmpty(ext) && IsVideoFile(itemName)) itemName = itemName[..^ext.Length];
        var sidecarDir = SidecarDirectory(itemDir, rootPath);
        var inDir = imagePaths.Where(p => DirectoryOf(p) == sidecarDir).ToList();

        string? Pick(string kind)
        {
            var candidates = inDir
                .Where(p => string.Equals(ClassifyImageFile(p), kind, StringComparison.Ordinal))
                .ToList();
            if (candidates.Count == 0) return null;
            var exact = candidates.FirstOrDefault(p => FileNameWithoutExt(p).Equals(kind, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
            var variant = candidates.FirstOrDefault(p => FileNameWithoutExt(p)
                .Equals($"{itemName}-{kind}", StringComparison.OrdinalIgnoreCase));
            if (variant is not null) return variant;
            return candidates.OrderBy(p => p, StringComparer.Ordinal).First();
        }
        return (Pick("poster"), Pick("backdrop"));
    }

    /// <summary>取路径的文件名或目录名（去掉扩展名的语义由调用方按需决定，这里保留全名）。</summary>
    private static string BaseName(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    /// <summary>取路径末段文件名并去掉扩展名（"Movie A.nfo" → "Movie A"）。</summary>
    private static string FileNameWithoutExt(string path)
        => Path.GetFileNameWithoutExtension(BaseName(path));

    /// <summary>取路径的目录部分；根层返回 "/"。</summary>
    private static string DirectoryOf(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        if (slash < 0) return "/";
        return slash == 0 ? "/" : trimmed[..slash];
    }

    /// <summary>把视频文件映射到条目目录：视频所在目录；库根下的散文件以文件路径本身为条目键。</summary>
    internal static string ResolveItemDirectory(string filePath, string rootPath)
    {
        var slash = filePath.LastIndexOf('/');
        if (slash <= 0) return filePath;
        var parent = filePath[..slash];
        // 视频直接位于库根（或目录层级不明确时），用文件本身作为条目，
        // 避免把整个库当成一个条目。
        if (parent == rootPath || parent == "/") return filePath;
        return parent;
    }

    /// <summary>Mixed 库按目录层级判定类型：条目目录含"Season/第x季"特征视为剧集。</summary>
    internal static MediaLibraryType ResolveItemType(MediaLibraryType libraryType, string rootPath, string itemDir)
    {
        if (libraryType != MediaLibraryType.Mixed) return libraryType;
        return SeasonRegex().IsMatch(itemDir) ? MediaLibraryType.TvShow : MediaLibraryType.Movie;
    }

    /// <summary>从条目目录或文件名解析标题与年份。</summary>
    internal static (string Title, int? Year) ParseTitle(string itemDir, string rootPath)
    {
        var name = LastSegment(itemDir, rootPath);

        // 剧集目录常形如 "Breaking Bad/Season 1"：条目名应取剧名，"Season 1" 只是季目录。
        // 库根下直接放 "Season 1" 时无法再往上取，保留原名。
        if (SeasonRegex().IsMatch(name))
        {
            var parent = ParentDirectory(itemDir);
            if (parent is not null && parent != rootPath && parent != "/")
                name = LastSegment(parent, rootPath);
        }

        // 若条目键是文件路径，去掉扩展名。
        var ext = Path.GetExtension(name);
        if (!string.IsNullOrEmpty(ext) && VideoExtensions.Contains(ext)) name = name[..^ext.Length];

        // 优先取括号里的年份："Blade Runner 2049 (2017)" 的 2049 是片名的一部分，
        // 直接取首个 4 位数字会把标题切成 "Blade Runner"。只有没有括号年份时才退回裸数字。
        var yearMatch = ParenYearRegex().Match(name);
        if (!yearMatch.Success) yearMatch = YearRegex().Match(name);
        // 括号年份的捕获组是纯数字，裸数字则整体就是数字本身。
        var yearText = yearMatch.Groups["y"].Success ? yearMatch.Groups["y"].Value : yearMatch.Value;
        int? year = yearMatch.Success && int.TryParse(yearText, out var y) ? y : null;

        var title = name;
        if (yearMatch.Success) title = name[..yearMatch.Index];
        title = title.Replace('.', ' ').Replace('_', ' ');
        title = BracketRegex().Replace(title, " ");
        // 年份被截断后，"片名 (2014)" 会剩下未配对的左括号（成对括号已被 BracketRegex 处理），
        // 因此 Trim 必须同时覆盖半角/全角圆括号，否则标题会残留 "("。
        title = MultiSpaceRegex().Replace(title, " ")
            .Trim(' ', '-', '[', ']', '【', '】', '(', ')', '（', '）');
        if (string.IsNullOrWhiteSpace(title)) title = name;
        return (title, year);
    }

    [GeneratedRegex(@"\b(19|20)\d{2}\b")]
    private static partial Regex YearRegex();

    /// <summary>取路径末段作为名字；库根自身或空段回退为 <c>Unknown</c>（空标题会渲染成空白卡片）。</summary>
    private static string LastSegment(string path, string rootPath)
    {
        var raw = path;
        if (raw == rootPath || raw == "/") raw = raw.TrimEnd('/');
        var slash = raw.LastIndexOf('/');
        var name = slash >= 0 ? raw[(slash + 1)..] : raw;
        return string.IsNullOrWhiteSpace(name) ? "Unknown" : name;
    }

    /// <summary>取父目录；已在根层时返回 null。</summary>
    private static string? ParentDirectory(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        if (slash <= 0) return null;
        return trimmed[..slash];
    }

    /// <summary>括号（半角/方角/全角）包裹的年份，优先于裸数字。</summary>
    [GeneratedRegex(@"[\[\(【（]\s*(?<y>(19|20)\d{2})\s*[\]\)】）]")]
    private static partial Regex ParenYearRegex();

    [GeneratedRegex(@"[\[\(【（][^\]\)】）]*[\]\)】）]")]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();

    // ------------------------------------------------------------------
    // 番号识别：从条目名（目录名或文件名）解析生产编号（如 MIDE-883）。
    // 与标题解析互相独立 —— 番号是番号域的主检索键，解析不到就返回 null，不影响标题。
    // ------------------------------------------------------------------

    /// <summary>
    /// 从条目名解析番号；解析不到返回 null。输出统一为大写并以连字符分隔。
    /// 识别优先级（先命中先用）：
    /// <list type="number">
    /// <item>括号/方括号包裹的编号（最可靠的显式信号，不做年份排除）；</item>
    /// <item>FC2 系列（FC2-PPV-1234567 / FC2PPV-1234567 / FC2-1234567）；</item>
    /// <item>带连字符的常规编号（abc-123）；</item>
    /// <item>字母数字连写（abc123）。</item>
    /// </list>
    /// 3、4 两种形态会排除疑似年份的数字段（1900-2099 的纯 4 位数，如
    /// "Blade2049" 不算番号）；1、2 属于明确标记，不做排除。
    /// </summary>
    internal static string? ExtractCode(string itemName)
    {
        var name = itemName;
        var ext = Path.GetExtension(name);
        if (!string.IsNullOrEmpty(ext) && VideoExtensions.Contains(ext)) name = name[..^ext.Length];

        var bracketed = BracketedCodeRegex().Match(name);
        if (bracketed.Success) return Format(bracketed.Groups["l"].Value, bracketed.Groups["n"].Value);

        var fc2 = Fc2CodeRegex().Match(name);
        if (fc2.Success)
            return fc2.Groups["ppv"].Success ? $"FC2-PPV-{fc2.Groups["n"].Value}" : $"FC2-{fc2.Groups["n"].Value}";

        var plain = PlainCodeRegex().Match(name);
        if (plain.Success && !LooksLikeYear(plain.Groups["n"].Value))
            return Format(plain.Groups["l"].Value, plain.Groups["n"].Value);

        var joined = JoinedCodeRegex().Match(name);
        if (joined.Success && !LooksLikeYear(joined.Groups["n"].Value))
            return Format(joined.Groups["l"].Value, joined.Groups["n"].Value);

        return null;
    }

    /// <summary>番号的规范形态：大写字母部分 + 连字符 + 数字部分。</summary>
    private static string Format(string letters, string digits)
        => $"{letters.ToUpperInvariant()}-{digits}";

    /// <summary>数字段是否疑似年份（纯 4 位、1900-2099、无前导零）。</summary>
    private static bool LooksLikeYear(string digits)
        => digits.Length == 4 && digits[0] is '1' or '2'
            && int.TryParse(digits, out var y) && y is >= 1900 and <= 2099;

    /// <summary>括号/方括号（含全角）包裹的番号：[MIDE-883]、(abp-123)。</summary>
    [GeneratedRegex(@"[\[\(【（]\s*(?<l>[A-Za-z][A-Za-z0-9]{1,9})-(?<n>\d{2,7})\s*[\]\)】）]")]
    private static partial Regex BracketedCodeRegex();

    /// <summary>FC2 系列编号，PPV 段可选。</summary>
    [GeneratedRegex(@"\bFC2(?:[-_ ]?(?<ppv>PPV))?[-_ ]?(?<n>\d{6,10})\b", RegexOptions.IgnoreCase)]
    private static partial Regex Fc2CodeRegex();

    /// <summary>带连字符的常规编号：MIDE-883、ssis-007。</summary>
    [GeneratedRegex(@"\b(?<l>[A-Za-z]{2,8})-(?<n>\d{2,7})\b")]
    private static partial Regex PlainCodeRegex();

    /// <summary>字母数字连写：MIDE883、abp123。</summary>
    [GeneratedRegex(@"\b(?<l>[A-Za-z]{2,8})(?<n>\d{3,7})\b")]
    private static partial Regex JoinedCodeRegex();

    [GeneratedRegex(@"(?i)(season\s*\d+|第\s*[0-9一二三四五六七八九十]+\s*季|s\d{1,2})")]
    private static partial Regex SeasonRegex();
}
