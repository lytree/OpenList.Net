using OpenList.Media.Domain;

namespace OpenList.Media.Services;

/// <summary>
/// 关注 / 想看 / 下载三块共用的外部作品工具：媒体类型归一、类型映射、落盘目录名清洗。
/// <para>
/// 放在一处是因为这三块必须用同一套口径：更新流里的 <c>media_type</c> 是 TMDB 的
/// <c>movie</c>/<c>tv</c>，而库里是 <see cref="MediaLibraryType"/>，一旦两处各写一份
/// switch，movie/tv 撞号时就会出现「更新流里还显示未入库、库里其实已经有了」。
/// </para>
/// </summary>
internal static class MediaExternal
{
    /// <summary>目录名长度上限：留够空间给库根路径与文件系统 255 字节的限制。</summary>
    private const int DirectoryNameMaxLength = 80;

    /// <summary>把数据源的类型字符串归一成 <c>movie</c> / <c>tv</c>（认不出按 movie）。</summary>
    public static string NormalizeType(string? mediaType)
        => string.Equals((mediaType ?? string.Empty).Trim(), "tv", StringComparison.OrdinalIgnoreCase)
            ? "tv"
            : "movie";

    /// <summary>数据源类型 → 媒体库类型。</summary>
    public static MediaLibraryType ToLibraryType(string? mediaType)
        => NormalizeType(mediaType) == "tv" ? MediaLibraryType.TvShow : MediaLibraryType.Movie;

    /// <summary>
    /// 把作品标题变成可以安全拼进 VFS 路径的目录名。
    /// <para>
    /// 只做「不越界 + 合法」这一件事，不改写业务命名：下载目录最终会当扫描根目录用，
    /// 一旦出现 <c>/</c> 或 <c>..</c>，下载器就会往库外写文件。非法字符统一换成下划线，
    /// 连续空白压成一个空格，首尾的点与空格去掉（Windows 上尾点会被静默吞掉）。
    /// </para>
    /// </summary>
    public static string SanitizeDirectoryName(string? title)
    {
        var text = (title ?? string.Empty).Trim();
        if (text.Length == 0) return "media";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = new char[text.Length];
        var len = 0;
        var lastWasSpace = false;
        foreach (var c in text)
        {
            // 路径分隔符与冒号在任何平台都必须挡住：它们不是「文件名里可以有的字符」。
            var bad = c is '/' or '\\' or ':' || Array.IndexOf(invalid, c) >= 0;
            var ch = bad ? '_' : c;
            if (ch == ' ')
            {
                if (lastWasSpace) continue;
                lastWasSpace = true;
            }
            else
            {
                lastWasSpace = false;
            }
            chars[len++] = ch;
        }

        var name = new string(chars, 0, len).Trim(' ', '.');
        if (name.Length > DirectoryNameMaxLength) name = name[..DirectoryNameMaxLength].Trim(' ', '.');
        // 全是非法字符时上面的结果会是 "____" 之类，仍有名字可用；只有真的空了才兜底。
        return name.Length == 0 ? "media" : name;
    }
}
