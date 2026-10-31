using System.Text;
using System.Web;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;

namespace OpenList.Server.Endpoints;

/// <summary>
/// 杂项公开端点：/i/{linkName}（iOS 安装用 Plist manifest）、/manifest.json（PWA 清单）、
/// /favicon.ico、/robots.txt。
/// 对应 OpenList Go <c>server/handles/helper.go::Plist</c> 以及
/// <c>server/server.go</c> 中注册的静态资源路由。
/// </summary>
public static class MiscEndpoints
{
    /// <summary><c>robots_txt</c> 设置为空时的默认内容（对齐 Go 的固定返回值）。</summary>
    private const string DefaultRobotsTxt = "User-agent: *\nAllow: /\n";

    /// <summary>
    /// 注册 /i、/manifest.json、/robots.txt、/favicon.ico 等公开路由。
    /// /i 解析 base64 编码的链接并生成 Apple plist（用于在线安装 IPA）；
    /// 其余为 PWA 相关静态资源，均匿名可访问。
    /// </summary>
    public static IEndpointRouteBuilder MapMiscApi(this IEndpointRouteBuilder app)
    {
        // GET /i/{linkName} — 生成 iOS 安装 IPA 所需的 Plist manifest。
        // linkName = base64( urlEncoded(rawUrl) + "/" + urlEncoded(fileName) ) + ".plist"
        app.MapGet("/i/{linkName}", (string linkName) => BuildPlist(linkName));

        // GET /manifest.json — PWA Web 应用清单。从设置读取 site_title
        // （未配置时回退为 "OpenList"）。前端的 index.html 会引用此文件。
        app.MapGet("/manifest.json", async (ISettingService settings) =>
        {
            var pub = await settings.GetPublicAsync();
            var title = pub.Data?.TryGetValue("site_title", out var t) == true && !string.IsNullOrEmpty(t)
                ? t : "OpenList";
            var manifest = new
            {
                name = title,
                short_name = title,
                start_url = ".",
                display = "standalone",
                background_color = "#000000",
                theme_color = "#000000",
                description = "OpenList - A file list program",
            };
            return Results.Json(manifest);
        }).AllowAnonymous();

        // GET /robots.txt — 返回设置项 robots_txt 的内容；未配置时用「允许所有爬虫」的默认串。
        // 以前是硬编码固定串，设置页里的 robots_txt 形同虚设（见 docs/settings-gap-audit.md）。
        app.MapGet("/robots.txt", async (ISettingService settings) =>
        {
            var pub = await settings.GetPublicAsync();
            var custom = pub.Data?.TryGetValue("robots_txt", out var v) == true ? v : null;
            var body = string.IsNullOrWhiteSpace(custom) ? DefaultRobotsTxt : custom!;
            return Results.Text(body, "text/plain", Encoding.UTF8);
        }).AllowAnonymous();

        // GET /favicon.ico — 302 跳转到配置的 favicon（回退 logo）地址，或返回 404。
        // 设置优先级：favicon → logo。favicon 是浏览器标签页图标，logo 是站内品牌图；
        // 以前只读 logo，导致设置页里的 favicon 配了不生效（见 docs/settings-gap-audit.md P0-2）。
        app.MapGet("/favicon.ico", async (ISettingService settings) =>
        {
            var pub = await settings.GetPublicAsync();
            string? Pick(string key) =>
                pub.Data?.TryGetValue(key, out var v) == true && !string.IsNullOrWhiteSpace(v) ? v : null;
            var icon = Pick("favicon") ?? Pick("logo");
            if (!string.IsNullOrEmpty(icon))
                return Results.Redirect(icon, permanent: false);
            return Results.NotFound();
        }).AllowAnonymous();

        return app;
    }

    /// <summary>
    /// 将 base64 编码的链接解码并构建 Apple plist XML。
    /// 链接格式为 <c>urlEncoded(rawUrl)/urlEncoded(fullName)</c>（可带
    /// <c>@identifier</c> 后缀）；解出的内容会被 XML/HTML 转义以防注入。
    /// </summary>
    private static IResult BuildPlist(string linkName)
    {
        // 去掉可选的 ".plist" 后缀。
        var b64 = linkName.EndsWith(".plist", StringComparison.OrdinalIgnoreCase)
            ? linkName[..^".plist".Length]
            : linkName;

        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
        catch { return Results.BadRequest("invalid base64 link name"); }

        // 分隔符 "/" 将解码串拆为 rawUrl 与 fullName 两部分。
        var parts = decoded.Split('/');
        if (parts.Length != 2)
            return Results.BadRequest("malformed link");

        // parts[0] = urlEncoded(rawUrl)；parts[1] = urlEncoded(fullName)。
        var rawUrl = HttpUtility.UrlDecode(parts[0]);
        var fullName = HttpUtility.UrlDecode(parts[1]);

        // fullName 可能带有 "@identifier" 后缀 —— 按 "@" 拆出应用名与包标识。
        var name = fullName;
        var identifier = $"org.opolist.{fullName}";
        var atIdx = fullName.LastIndexOf('@');
        if (atIdx >= 0)
        {
            name = fullName[..atIdx];
            identifier = fullName[(atIdx + 1)..];
        }

        // 转义用于 XML 输出的特殊字符。
        var url = rawUrl.Replace("<", "&lt;").Replace(">", "&gt;");
        name = HttpUtility.HtmlEncode(name);
        identifier = HttpUtility.HtmlEncode(identifier);

        var plist = $@"<?xml version=""1.0"" encoding=""UTF-8""?><!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
    <dict>
        <key>items</key>
        <array>
            <dict>
                <key>assets</key>
                <array>
                    <dict>
                        <key>kind</key>
                        <string>software-package</string>
                        <key>url</key>
                        <string><![CDATA[{url}]]></string>
                    </dict>
                </array>
                <key>metadata</key>
                <dict>
                    <key>bundle-identifier</key>
					<string>{identifier}</string>
					<key>bundle-version</key>
                    <string>4.4</string>
                    <key>kind</key>
                    <string>software</string>
                    <key>title</key>
                    <string>{name}</string>
                </dict>
            </dict>
        </array>
    </dict>
</plist>";
        return Results.Text(plist, "application/xml", Encoding.UTF8);
    }
}
