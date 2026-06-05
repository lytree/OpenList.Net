using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenList.Core.Interfaces;
using OpenList.Server.Models;

namespace OpenList.Server.Endpoints;

public static class PublicEndpoints
{
    public static IEndpointRouteBuilder MapPublicApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/public");

        group.MapGet("settings", async (ISettingService settings) =>
            BuildSettingsResp(settings));
        group.MapPost("settings", async (ISettingService settings) =>
            BuildSettingsResp(settings));

        group.MapGet("offline_download_tools", () =>
            Results.Json(new Resp<List<string>>(["aria2", "qBittorrent"])));
        group.MapPost("offline_download_tools", () =>
            Results.Json(new Resp<List<string>>(["aria2", "qBittorrent"])));

        group.MapGet("archive_extensions", () =>
            Results.Json(new Resp<List<string>>([".zip", ".tar", ".tar.gz", ".tgz", ".tar.bz2", ".tbz2", ".tar.xz", ".txz", ".tar.zst", ".tzst", ".rar", ".7z"])));
        group.MapPost("archive_extensions", () =>
            Results.Json(new Resp<List<string>>([".zip", ".tar", ".tar.gz", ".tgz", ".tar.bz2", ".tbz2", ".tar.xz", ".txz", ".tar.zst", ".tzst", ".rar", ".7z"])));

        return endpoints;
    }

    private static IResult BuildSettingsResp(ISettingService settings)
    {
        return Results.Json(new Resp<PublicSettingsResp>(new PublicSettingsResp
        {
            Title = settings.GetString("title") ?? "OpenList",
            Logo = settings.GetString("logo") ?? "",
            Favicon = settings.GetString("favicon") ?? "",
            Footer = settings.GetString("footer") ?? "",
            CustomHead = settings.GetString("custom_head") ?? "",
            VideoTypes = settings.GetString("video_types") ?? "",
            AudioTypes = settings.GetString("audio_types") ?? "",
            TextTypes = settings.GetString("text_types") ?? "",
            SsoEnabled = settings.GetBool("sso_login_enabled"),
            SsoLoginPlatform = settings.GetString("sso_login_platform") ?? "",
            SsoCompatibilityMode = settings.GetBool("sso_compatibility_mode"),
            LdapEnabled = settings.GetBool("ldap_login_enabled"),
            LdapLoginTips = settings.GetString("ldap_login_tips") ?? "",
            WebauthnLoginEnabled = settings.GetBool("webauthn_login_enabled"),
            SharePreview = settings.GetBool("share_preview"),
            ShareArchivePreview = settings.GetBool("share_archive_preview"),
            ShowDiskUsageInPlainText = settings.GetBool("show_disk_usage_in_plain_text")
        }));
    }
}
