using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using OpenList.Desktop.Localization;

namespace OpenList.Desktop.Services;

/// <summary>
/// 更新检查：查本项目（<see cref="AppInfo.Repo"/>）在 GitHub 上的最新 release，
/// 有新版本时引导用户打开发布页。版本号由 <see cref="AppInfo"/> 从程序集读取，
/// 与发布时注入的 semver 一致。
/// </summary>
public sealed class UpdateChecker
{
    public sealed record ReleaseInfo(string Tag, string Name, string Url);

    public sealed class GhRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private readonly HttpClient _http = new();
    private readonly LogEventBus _log;

    public UpdateChecker(LogEventBus log)
    {
        _log = log;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenList-Desktop-Net");
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    /// <summary>返回最新 release 信息；失败返回 null（错误已写日志）。</summary>
    public async Task<ReleaseInfo?> CheckAsync()
    {
        try
        {
            var r = await _http.GetFromJsonAsync<GhRelease>(AppInfo.ReleasesApi);
            if (r is null) return null;
            return new ReleaseInfo(r.TagName, r.Name,
                string.IsNullOrEmpty(r.HtmlUrl) ? AppInfo.ReleasesPage : r.HtmlUrl);
        }
        catch (Exception ex)
        {
            _log.Error("Update", Localizer.Format("upd.checkFailedLog", ex.Message));
            return null;
        }
    }

    /// <summary>列出最近的 releases（新→旧），供"选择版本"。repo 形如 "owner/name"。</summary>
    public async Task<List<ReleaseInfo>> ListReleasesAsync(string repo = AppInfo.Repo, int take = 10)
    {
        try
        {
            var list = await _http.GetFromJsonAsync<List<GhRelease>>(
                $"https://api.github.com/repos/{repo}/releases?per_page={take}");
            if (list is null) return [];
            return [.. list.Select(r => new ReleaseInfo(
                r.TagName, r.Name, string.IsNullOrEmpty(r.HtmlUrl) ? $"https://github.com/{repo}/releases" : r.HtmlUrl))];
        }
        catch (Exception ex)
        {
            _log.Error("Update", Localizer.Format("upd.listFailedLog", ex.Message));
            return [];
        }
    }

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* 打不开就算了 */ }
    }
}
