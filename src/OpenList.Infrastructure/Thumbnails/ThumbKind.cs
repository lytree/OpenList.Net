namespace OpenList.Infrastructure.Thumbnails;

/// <summary>
/// 缩略图生成类型。判定只看扩展名（零 IO），因此可以在列表页的每个条目上调用。
/// </summary>
public enum ThumbKind
{
    /// <summary>不支持（文本、压缩包、SVG 等）。</summary>
    None = 0,

    /// <summary>静态图片：一次解码 + 缩放。</summary>
    Image = 1,

    /// <summary>音频：取内嵌封面（ID3 APIC / FLAC PICTURE / MP4 covr）。</summary>
    Audio = 2,

    /// <summary>视频：抽一帧。成本最高，且需要可随机读的源。</summary>
    Video = 3,

    /// <summary>PDF：首页渲染。<b>当前未实现</b>（需要 PDFium，见 docs/thumbnail-service-plan.md §5）。</summary>
    Pdf = 4,
}

/// <summary>
/// 扩展名 → <see cref="ThumbKind"/> 的映射表，以及「该类型能否生成」的判定。
/// <para>
/// 刻意<b>排除 svg</b>：SVG 缩略图等于给站点开一个存储型 XSS 入口（脚本随图执行），
/// 而 ffmpeg 也不解 SVG，两边一致地不支持是最省事的正确解。
/// </para>
/// </summary>
public static class ThumbKinds
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "webp", "bmp", "gif", "avif", "tif", "tiff", "ico",
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "flac", "m4a", "ogg", "opus", "wav", "wma", "aac", "ape",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "mkv", "mov", "webm", "avi", "flv", "wmv", "m4v", "ts", "rmvb",
    };

    /// <summary>按文件名（取最后一个 '.' 之后的扩展名）判定类型。</summary>
    public static ThumbKind FromName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return ThumbKind.None;
        var dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1) return ThumbKind.None;
        return FromExtension(name[(dot + 1)..]);
    }

    /// <summary>按裸扩展名（不带点）判定类型。</summary>
    public static ThumbKind FromExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension)) return ThumbKind.None;
        var ext = extension[0] == '.' ? extension[1..] : extension;
        if (ImageExtensions.Contains(ext)) return ThumbKind.Image;
        if (AudioExtensions.Contains(ext)) return ThumbKind.Audio;
        if (VideoExtensions.Contains(ext)) return ThumbKind.Video;
        if (ext.Equals("pdf", StringComparison.OrdinalIgnoreCase)) return ThumbKind.Pdf;
        return ThumbKind.None;
    }

    /// <summary>
    /// 列表/网格是否可以为该类型下发 <c>thumb</c>。
    /// <para>
    /// 视频被排除在外：一个 20 个视频的目录会让浏览器立刻发起 20 次远端抽帧，
    /// 直接把云盘配额打掉。视频缩略图只在详情页显式请求（见计划文档 §15 风险清单）。
    /// </para>
    /// </summary>
    public static bool IsListSafe(ThumbKind kind) => kind is ThumbKind.Image or ThumbKind.Audio;

    /// <summary>该类型是否需要"随机读"（视频要 seek，图片/音频整读即可）。</summary>
    public static bool NeedsRandomAccess(ThumbKind kind) => kind == ThumbKind.Video;
}
