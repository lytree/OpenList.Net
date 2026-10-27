namespace OpenList.Media.Services;

/// <summary>
/// 人物头像在「NFO 文本」与「VFS 路径」之间的换算（纯函数，便于单测）。
/// <para>
/// 两个方向的口径不同，别合并成一个方法：
/// <list type="bullet">
/// <item>读：NFO 里的 <c>&lt;thumb&gt;</c> 通常是**相对条目目录**的文件名
///   （<c>harrison.jpg</c>），也可能是绝对 URL（TMDB）——要还原成能直接读的路径；</item>
/// <item>写：落盘的 NFO 要能拿到别的机器上用，所以本地图**只写文件名**，
///   写绝对 VFS 路径会让 NFO 绑死在这台服务器的挂载布局上。</item>
/// </list>
/// </para>
/// </summary>
internal static class MediaPersonImage
{
    /// <summary>NFO 的 thumb 值 → 可直接读取的路径；空/非法返回 null。</summary>
    /// <param name="raw">NFO 里的原始值。</param>
    /// <param name="itemDir">条目目录（VFS 绝对路径），相对值按它解析。</param>
    internal static string? ToVfsPath(string? raw, string itemDir)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = raw.Trim();

        // TMdb 刮削的结果是绝对 URL，原样交给上层做 302
        if (IsRemote(value)) return value;

        // 已经是 VFS 绝对路径（手工编辑过的 NFO 偶尔会这么写）
        if (value.StartsWith('/')) return Normalize(value);

        // 相对值：拒绝 `..` 越界。NFO 是用户可写内容，不能让它把读取引到条目目录之外
        var segments = value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s == "..")) return null;

        var dir = itemDir.TrimEnd('/');
        return Normalize(dir + "/" + string.Join('/', segments));
    }

    /// <summary>落库路径 → 写回 NFO 的 thumb 值；空返回 null。</summary>
    internal static string? ToNfoValue(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var value = path.Trim();
        if (IsRemote(value)) return value;
        var name = Path.GetFileName(value);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static bool IsRemote(string value)
        => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>折叠重复斜杠、去掉结尾斜杠（`//e2e//a.jpg` → `/e2e/a.jpg`）。</summary>
    private static string Normalize(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return "/" + string.Join('/', parts);
    }
}
