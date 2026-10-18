using Microsoft.AspNetCore.Http;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 站点基地址解析器，镜像 Go <c>server/common/base.go</c> 的 <c>GetApiUrlFromRequest</c>。
/// <para>
/// 顺序：
/// <list type="number">
/// <item>配置 <c>SiteUrl</c> 以 <c>http</c> 开头 → 直接作为完整地址（去掉末尾 <c>/</c>）。</item>
/// <item>否则取请求的 <c>X-Forwarded-Proto</c> / <c>X-Forwarded-Host</c>（反代场景），
/// 缺失时回退到 <c>Request.Scheme</c> / <c>Request.Host</c>，再拼上 <see cref="HttpRequest.PathBase"/>。</item>
/// <item><c>SiteUrl</c> 若是不以 <c>http</c> 开头的路径（如 <c>/openlist</c>），当作路径前缀追加。</item>
/// <item>无 HTTP 上下文（后台任务、单元测试）且未配置 <c>SiteUrl</c> → 返回空串，调用方据此退回相对地址。</item>
/// </list>
/// </para>
/// </summary>
public sealed class PublicUrlResolver : IPublicUrlResolver
{
    private readonly AppConfiguration _config;
    private readonly IHttpContextAccessor? _http;

    /// <summary>创建解析器实例。</summary>
    /// <param name="config">应用配置（含 <c>SiteUrl</c>）。</param>
    /// <param name="http">HTTP 上下文访问器（可选；缺省时只能用配置值）。</param>
    public PublicUrlResolver(AppConfiguration config, IHttpContextAccessor? http = null)
    {
        _config = config;
        _http = http;
    }

    /// <inheritdoc/>
    public string Resolve()
    {
        var site = _config.SiteUrl ?? string.Empty;
        site = site.Trim();

        // 配成完整地址：直接采用，不再看请求头（反代后 Host 往往不是对外域名）。
        if (site.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || site.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return site.TrimEnd('/');
        }

        var ctx = _http?.HttpContext;
        if (ctx is null) return site.TrimEnd('/');

        var request = ctx.Request;
        var scheme = FirstHeader(request, "X-Forwarded-Proto") ?? request.Scheme;
        var host = FirstHeader(request, "X-Forwarded-Host") ?? request.Host.Value;
        if (string.IsNullOrEmpty(host)) return site.TrimEnd('/');

        var pathBase = request.PathBase.HasValue ? request.PathBase.Value : string.Empty;
        var prefix = site.Length == 0 ? string.Empty : "/" + site.Trim('/');
        return $"{scheme}://{host}{pathBase}{prefix}".TrimEnd('/');
    }

    /// <summary>取转发头的第一个值（可能形如 <c>https, http</c>，只取首个）。</summary>
    private static string? FirstHeader(HttpRequest request, string name)
    {
        if (!request.Headers.TryGetValue(name, out var values)) return null;
        var raw = values.ToString();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var first = raw.Split(',')[0].Trim();
        return first.Length == 0 ? null : first;
    }
}
