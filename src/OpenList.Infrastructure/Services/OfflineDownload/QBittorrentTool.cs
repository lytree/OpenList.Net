using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenList.Core.Interfaces;

namespace OpenList.Infrastructure.Services.OfflineDownload;

/// <summary>
/// qBittorrent Web API client for offline downloads
/// </summary>
public class QBittorrentTool : IOfflineDownloadTool
{
    private readonly HttpClient _http;
    private readonly string _url;
    private readonly string _username;
    private readonly string _password;
    private bool _ready;
    private int _seedTimeMinutes;

    public string Name => "qBittorrent";
    public bool IsReady => _ready;

    public QBittorrentTool(string url, string username, string password, int seedTimeMinutes = 0)
    {
        _url = url.TrimEnd('/');
        _username = username;
        _password = password;
        _seedTimeMinutes = seedTimeMinutes;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<string?> InitAsync(CancellationToken ct = default)
    {
        _ready = false;
        try
        {
            // Login to qBittorrent Web UI
            var loginContent = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = _username,
                ["password"] = _password
            });

            var loginResp = await _http.PostAsync($"{_url}/api/v2/auth/login", loginContent, ct);
            loginResp.EnsureSuccessStatusCode();
            var loginBody = await loginResp.Content.ReadAsStringAsync(ct);
            if (loginBody.Trim() != "Ok." && loginBody.Trim() != "Ok")
                return $"qBittorrent login failed: {loginBody}";

            // Get version
            var versionResp = await _http.GetStringAsync($"{_url}/api/v2/app/version", ct);
            _ready = true;
            return $"qBittorrent version: {versionResp}";
        }
        catch (Exception ex)
        {
            return $"Failed to connect to qBittorrent: {ex.Message}";
        }
    }

    public async Task<string> AddUrlAsync(AddUrlArgs args, CancellationToken ct = default)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(args.Url), "urls" },
            { new StringContent(args.TempDir), "savepath" },
            { new StringContent(args.TaskId), "rename" }
        };

        var response = await _http.PostAsync($"{_url}/api/v2/torrents/add", content, ct);
        response.EnsureSuccessStatusCode();

        return args.TaskId; // Use task ID as GID
    }

    public async Task RemoveAsync(string gid, CancellationToken ct = default)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["hashes"] = gid,
            ["deleteFiles"] = "true"
        });

        await _http.PostAsync($"{_url}/api/v2/torrents/delete", content, ct);
    }

    public async Task<DownloadStatus> GetStatusAsync(string gid, CancellationToken ct = default)
    {
        var torrents = await _http.GetFromJsonAsync<List<QBitTorrentInfo>>(
            $"{_url}/api/v2/torrents/info?hashes={gid}", ct);

        var info = torrents?.FirstOrDefault();
        if (info == null)
            return new DownloadStatus { Error = "Torrent not found", StatusText = "not found" };

        var status = new DownloadStatus
        {
            TotalBytes = info.Size,
            Progress = info.Size > 0 ? (double)info.Completed / info.Size * 100 : 0,
        };

        // Completed states
        if (info.State is "uploading" or "pausedUP" or "queuedUP" or "stalledUP"
            or "forcedUP" or "checkingUP")
        {
            status.Completed = true;
            status.StatusText = "completed";
        }
        // Downloading states
        else if (info.State is "allocating" or "downloading" or "metaDL" or "pausedDL"
            or "queuedDL" or "stalledDL" or "checkingDL" or "forcedDL"
            or "checkingResumeData" or "moving")
        {
            status.StatusText = $"downloading ({status.Progress:F1}%)";
        }
        // Error states
        else if (info.State is "error" or "missingFiles" or "unknown")
        {
            status.Error = $"qBittorrent error: {info.State}";
            status.StatusText = $"error: {info.State}";
        }
        else
        {
            status.StatusText = info.State;
        }

        return status;
    }

    private class QBitTorrentInfo
    {
        [JsonPropertyName("hash")]
        public string Hash { get; set; } = "";
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
        [JsonPropertyName("size")]
        public long Size { get; set; }
        [JsonPropertyName("completed")]
        public long Completed { get; set; }
        [JsonPropertyName("state")]
        public string State { get; set; } = "";
        [JsonPropertyName("dlspeed")]
        public long DownloadSpeed { get; set; }
        [JsonPropertyName("upspeed")]
        public long UploadSpeed { get; set; }
    }
}
