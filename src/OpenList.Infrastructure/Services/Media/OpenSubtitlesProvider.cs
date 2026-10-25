using System.Text.Json;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.Services.Media;

/// <summary>
/// OpenSubtitles.com REST API provider（v1）。
/// 需要设置项 <c>media_opensubtitles_api_key</c>；未配置时搜索返回空。
/// 搜索使用 <c>/subtitles</c>，下载先用 <c>/download</c> 换取临时直链再拉取内容。
/// </summary>
public sealed class OpenSubtitlesProvider : ISubtitleProvider
{
    private const string ApiBase = "https://api.opensubtitles.com/api/v1";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ISettingService _settings;

    /// <summary>创建 OpenSubtitles provider 实例。</summary>
    public OpenSubtitlesProvider(IHttpClientFactory httpFactory, ISettingService settings)
    {
        _httpFactory = httpFactory;
        _settings = settings;
    }

    /// <inheritdoc />
    public string Name => "opensubtitles";

    /// <inheritdoc />
    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
        => !string.IsNullOrWhiteSpace(await GetApiKeyAsync(ct));

    /// <inheritdoc />
    public async Task<List<SubtitleCandidate>> SearchAsync(string title, int? year, string language, CancellationToken ct = default)
    {
        var key = await GetApiKeyAsync(ct);
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(title)) return [];

        var url = $"{ApiBase}/subtitles?query={Uri.EscapeDataString(title)}&languages={Uri.EscapeDataString(NormalizeLanguage(language))}&per_page=20";
        if (year is not null) url += $"&year={year}";

        try
        {
            var client = _httpFactory.CreateClient("opensubtitles");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Api-Key", key);
            req.Headers.Add("User-Agent", "OpenList.Net v1.0");
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return [];

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync(ct));
            var list = new List<SubtitleCandidate>();
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return list;

            foreach (var e in data.EnumerateArray())
            {
                if (!e.TryGetProperty("attributes", out var attr)) continue;
                var files = attr.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array ? f.EnumerateArray().ToList() : [];
                if (files.Count == 0) continue;
                var file = files[0];
                var fileId = file.TryGetProperty("file_id", out var fid) ? fid.GetInt64().ToString() : null;
                if (string.IsNullOrWhiteSpace(fileId)) continue;

                list.Add(new SubtitleCandidate
                {
                    Id = fileId,
                    Name = file.TryGetProperty("file_name", out var fn) ? fn.GetString() ?? string.Empty : title,
                    Language = attr.TryGetProperty("language", out var lang) ? lang.GetString() ?? language : language,
                    Downloads = attr.TryGetProperty("download_count", out var dc) && dc.TryGetInt32(out var n) ? n : 0,
                    Format = file.TryGetProperty("file_name", out var fn2) && fn2.GetString()?.EndsWith(".ass", StringComparison.OrdinalIgnoreCase) == true ? "ass" : "srt",
                    DownloadToken = fileId,
                });
            }
            return list.OrderByDescending(c => c.Downloads).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<byte[]?> DownloadAsync(SubtitleCandidate candidate, CancellationToken ct = default)
    {
        var key = await GetApiKeyAsync(ct);
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(candidate.DownloadToken)) return null;

        try
        {
            var client = _httpFactory.CreateClient("opensubtitles");
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/download");
            req.Headers.Add("Api-Key", key);
            req.Headers.Add("User-Agent", "OpenList.Net v1.0");
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { file_id = long.Parse(candidate.DownloadToken) }),
                System.Text.Encoding.UTF8, "application/json");

            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync(ct));
            if (!doc.RootElement.TryGetProperty("link", out var linkEl) || linkEl.GetString() is not { } link) return null;

            using var fileResp = await client.GetAsync(link, ct);
            if (!fileResp.IsSuccessStatusCode) return null;
            return await fileResp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<string?> GetApiKeyAsync(CancellationToken ct)
    {
        var resp = await _settings.GetAsync("media_opensubtitles_api_key", ct);
        return resp.Code == 200 && !string.IsNullOrWhiteSpace(resp.Data?.Value) ? resp.Data!.Value.Trim() : null;
    }

    /// <summary>把 "zh-CN" 之类的语言标签换成 OpenSubtitles 使用的 "zh-cn"。</summary>
    private static string NormalizeLanguage(string language)
        => string.IsNullOrWhiteSpace(language) ? "zh-cn" : language.Trim().ToLowerInvariant();
}
