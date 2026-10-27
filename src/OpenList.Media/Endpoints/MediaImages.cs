using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Media.Endpoints;

/// <summary>
/// 媒体图片响应的共用实现：条目海报/背景图与人物头像走同一套逻辑。
/// <para>
/// 三种形态必须一致，否则「条目出图、人物不出图」这种差异会被当成 bug 排查半天：
/// <list type="bullet">
/// <item>路径为空 → 404（没有图不是错误，是常态）；</item>
/// <item>外链（TMDB 给的绝对 URL）→ 302，不在服务器上转一手；</item>
/// <item>VFS 路径 → 读出字节并带上内容类型，读不到（或无权）也是 404。</item>
/// </list>
/// </para>
/// 统一走 404 而不是 403：403 等于确认「这个路径/id 确实存在」，等于把枚举结果告诉对方
/// （与站内信越权同一口径，见 §34.2）。
/// </summary>
internal static class MediaImages
{
    /// <summary>单张图上限 8MB：海报是单张图，整读比开流简单；上限挡住把视频误配成图片。</summary>
    internal const long MaxBytes = 8 * 1024 * 1024;

    /// <summary>是否外链。只认 http/https，别的协议（file: 等）一律当相对路径交给 VFS。</summary>
    internal static bool IsRemote(string path)
        => path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>按扩展名判内容类型；认不出的一律按 jpeg（不少 CDN 图没有扩展名）。</summary>
    internal static string ContentTypeOf(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            _ => "image/jpeg",
        };

    /// <summary>把一条图片路径变成响应。<paramref name="path"/> 可空（返回 404）。</summary>
    internal static async Task<IResult> ServeAsync(
        string? path, User user, IFileSystemService fs, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return Results.NotFound();
        if (IsRemote(path)) return Results.Redirect(path, permanent: false);

        var read = await fs.ReadFileAsync(path, MaxBytes, user, ct);
        if (read.Code != 200 || read.Data is null) return Results.NotFound();
        return Results.File(read.Data, ContentTypeOf(path));
    }
}
