using OpenList.Core.Interfaces;

namespace OpenList.Infrastructure.Thumbnails;

/// <summary>
/// 给 <c>Obj.thumb</c> 拼 URL 的实现。
/// <para>
/// 形态与 Go 上游一致：<c>{baseUrl}/p/{path}?type=thumb&amp;w=&amp;h=</c>。
/// 复用 <c>/p/</c> 而不是另开前缀，是因为签名策略、匿名放行、限流前缀、HEAD 语义
/// 在 <c>SignEndpoints</c> 里已经齐了；新开一个前缀等于把这四套逻辑各抄一遍。
/// </para>
/// <para>
/// <b>列表只下发图片与音频</b>：视频缩略图要抽帧，一个 20 个视频的目录会让浏览器立刻
/// 发起 20 次（远端）抽帧请求。视频缩略图只在详情页显式请求 <c>?type=thumb</c>。
/// </para>
/// </summary>
public sealed class ThumbnailUrlBuilder : IThumbnailUrlBuilder
{
    private readonly Func<ThumbnailOptions> _options;
    private readonly IPublicUrlResolver? _publicUrls;
    private readonly ISignService? _signs;

    /// <summary>创建构造器（签名与基地址解析都可为空：单元测试与匿名站点不需要）。</summary>
    public ThumbnailUrlBuilder(
        ThumbnailOptionsProvider options,
        IPublicUrlResolver? publicUrls = null,
        ISignService? signs = null)
        : this(() => options.Current, publicUrls, signs)
    {
    }

    /// <summary>
    /// 以取值函数构造：便于单元测试直接给定快照，不依赖设置表与后台刷新。
    /// </summary>
    /// <param name="options">返回当前配置快照的函数。</param>
    /// <param name="publicUrls">站点基地址解析器（可空）。</param>
    /// <param name="signs">签名服务（可空）。</param>
    public ThumbnailUrlBuilder(
        Func<ThumbnailOptions> options,
        IPublicUrlResolver? publicUrls = null,
        ISignService? signs = null)
    {
        _options = options;
        _publicUrls = publicUrls;
        _signs = signs;
    }

    /// <inheritdoc />
    public string? Build(string path, string name, long size, bool isDir, SignContext signCtx, bool driverAllowsThumb = true)
    {
        if (isDir || size <= 0 || !driverAllowsThumb) return null;

        var options = _options();
        if (!options.Enabled) return null;

        var kind = ThumbKinds.FromName(name);
        if (!ThumbKinds.IsListSafe(kind)) return null;
        if (kind != ThumbKind.Image && kind != ThumbKind.Audio) return null;
        if (size > options.MaxSourceBytes) return null;

        var dot = name.LastIndexOf('.');
        var extension = dot < 0 ? string.Empty : name[(dot + 1)..];
        if (!options.AllowsExtension(extension)) return null;

        // 逐段编码以保留 '/' 作为路径分隔符（ASP.NET 通配路由需要），与 BuildRawUrl 同一口径。
        var encoded = string.Join('/', path.Split('/').SkipWhile(string.IsNullOrEmpty).Select(Uri.EscapeDataString));
        var edge = options.MaxSize.ToString();
        var url = $"{_publicUrls?.Resolve() ?? string.Empty}/p/{encoded}?type=thumb&w={edge}&h={edge}";

        if (signCtx.NeedSign && _signs is not null)
            url += "&sign=" + Uri.EscapeDataString(_signs.Sign(path, signCtx.ExpireAt));
        return url;
    }
}
