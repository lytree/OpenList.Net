using System.Text;
using System.Web;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;

namespace OpenList.Server.Endpoints;

/// <summary>
/// Misc public endpoints: Plist (iOS IPA manifest), manifest.json, favicon, robots.txt.
/// Mirrors OpenList Go <c>server/handles/helper.go::Plist</c> and the static asset
/// routes registered in <c>server/server.go</c>.
/// </summary>
public static class MiscEndpoints
{
    public static IEndpointRouteBuilder MapMiscApi(this IEndpointRouteBuilder app)
    {
        // GET /i/{linkName} — iOS Plist manifest for IPA install.
        // linkName = base64( urlEncoded(rawUrl) + "/" + urlEncoded(fileName) ) + ".plist"
        app.MapGet("/i/{linkName}", (string linkName) => BuildPlist(linkName));

        // GET /manifest.json — PWA web app manifest. Reads site_title from settings
        // (falls back to "OpenList"). The frontend's index.html references this.
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

        // GET /robots.txt — allow all crawlers. Go returns a static string.
        app.MapGet("/robots.txt", () =>
            Results.Text("User-agent: *\nAllow: /\n", "text/plain", Encoding.UTF8))
          .AllowAnonymous();

        // GET /favicon.ico — redirect to the configured logo (or a default).
        // Mirrors Go which serves the logo from settings; here we 302 to the
        // public logo setting value if set, otherwise a data URI is avoided in
        // favor of a 404 (browsers handle gracefully).
        app.MapGet("/favicon.ico", async (ISettingService settings) =>
        {
            var pub = await settings.GetPublicAsync();
            var logo = pub.Data?.TryGetValue("logo", out var l) == true ? l : null;
            if (!string.IsNullOrEmpty(logo))
                return Results.Redirect(logo, permanent: false);
            return Results.NotFound();
        }).AllowAnonymous();

        return app;
    }

    private static IResult BuildPlist(string linkName)
    {
        // Strip optional ".plist" suffix.
        var b64 = linkName.EndsWith(".plist", StringComparison.OrdinalIgnoreCase)
            ? linkName[..^".plist".Length]
            : linkName;

        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
        catch { return Results.BadRequest("invalid base64 link name"); }

        var parts = decoded.Split('/');
        if (parts.Length != 2)
            return Results.BadRequest("malformed link");

        // parts[0] = urlEncoded(rawUrl); parts[1] = urlEncoded(fullName)
        var rawUrl = HttpUtility.UrlDecode(parts[0]);
        var fullName = HttpUtility.UrlDecode(parts[1]);

        // fullName may contain "@identifier" suffix — split on "@".
        var name = fullName;
        var identifier = $"org.opolist.{fullName}";
        var atIdx = fullName.LastIndexOf('@');
        if (atIdx >= 0)
        {
            name = fullName[..atIdx];
            identifier = fullName[(atIdx + 1)..];
        }

        // Escape for XML.
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
