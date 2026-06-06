using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenList.Core.Interfaces;

namespace OpenList.Infrastructure.Services.OfflineDownload;

/// <summary>
/// Aria2 JSON-RPC client for offline downloads
/// </summary>
public class Aria2Tool : IOfflineDownloadTool
{
    private readonly HttpClient _http;
    private readonly string _uri;
    private readonly string _secret;
    private bool _ready;

    public string Name => "aria2";
    public bool IsReady => _ready;

    public Aria2Tool(string uri, string secret)
    {
        _uri = uri;
        _secret = secret;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<string?> InitAsync(CancellationToken ct = default)
    {
        _ready = false;
        try
        {
            var version = await SendRpcAsync<string>("aria2.getVersion", [], ct);
            _ready = true;
            return $"aria2 version: {version}";
        }
        catch (Exception ex)
        {
            return $"Failed to connect to aria2: {ex.Message}";
        }
    }

    public async Task<string> AddUrlAsync(AddUrlArgs args, CancellationToken ct = default)
    {
        var options = new Dictionary<string, string> { ["dir"] = args.TempDir };
        var gid = await SendRpcAsync<string>("aria2.addUri", [new[] { args.Url }, options], ct);
        return gid;
    }

    public async Task RemoveAsync(string gid, CancellationToken ct = default)
    {
        try { await SendRpcAsync<string>("aria2.remove", [gid], ct); }
        catch { /* ignore if already removed */ }
    }

    public async Task<DownloadStatus> GetStatusAsync(string gid, CancellationToken ct = default)
    {
        var info = await SendRpcAsync<Aria2Status>("aria2.tellStatus", [gid], ct);
        var total = long.TryParse(info.TotalLength, out var t) ? t : 0;
        var downloaded = long.TryParse(info.CompletedLength, out var d) ? d : 0;

        var status = new DownloadStatus
        {
            TotalBytes = total,
            Progress = total > 0 ? (double)downloaded / total * 100 : 0,
            Completed = info.Status == "complete",
        };

        // Handle follow-by (torrent -> download)
        if (info.FollowedBy is { Length: > 0 })
        {
            status.NewGID = info.FollowedBy[0];
        }

        status.StatusText = info.Status switch
        {
            "complete" => "completed",
            "error" => $"error: {info.ErrorMessage}",
            "active" when info.Seeder == "true" => "seeding",
            "active" => $"downloading ({status.Progress:F1}%)",
            "waiting" or "paused" => info.Status,
            "removed" => "removed",
            _ => info.Status
        };

        if (info.Status == "error")
            status.Error = info.ErrorMessage;
        if (info.Status == "removed")
            status.Error = "Download removed";

        return status;
    }

    private async Task<T> SendRpcAsync<T>(string method, object[] parameters, CancellationToken ct)
    {
        var rpcParams = string.IsNullOrEmpty(_secret)
            ? parameters
            : new object[] { $"token:{_secret}" }.Concat(parameters).ToArray();

        var request = new JsonRpcRequest
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Method = method,
            Params = rpcParams
        };

        var response = await _http.PostAsJsonAsync(_uri, request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonRpcResponse<T>>(ct);
        if (result?.Error != null)
            throw new InvalidOperationException($"aria2 RPC error: {result.Error.Message} (code: {result.Error.Code})");

        return result is { Result: not null } ? result.Result : default!;
    }

    // JSON-RPC models
    private class JsonRpcRequest
    {
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc => "2.0";
        public string Id { get; set; } = "";
        public string Method { get; set; } = "";
        public object[] Params { get; set; } = [];
    }

    private class JsonRpcResponse<T>
    {
        public string? Id { get; set; }
        public T? Result { get; set; }
        public JsonRpcError? Error { get; set; }
    }

    private class JsonRpcError
    {
        public int Code { get; set; }
        public string Message { get; set; } = "";
    }

    private class Aria2Status
    {
        [JsonPropertyName("gid")]
        public string Gid { get; set; } = "";
        [JsonPropertyName("status")]
        public string Status { get; set; } = "";
        [JsonPropertyName("totalLength")]
        public string TotalLength { get; set; } = "0";
        [JsonPropertyName("completedLength")]
        public string CompletedLength { get; set; } = "0";
        [JsonPropertyName("downloadSpeed")]
        public string DownloadSpeed { get; set; } = "0";
        [JsonPropertyName("errorMessage")]
        public string ErrorMessage { get; set; } = "";
        [JsonPropertyName("seeder")]
        public string Seeder { get; set; } = "";
        [JsonPropertyName("followedBy")]
        public string[]? FollowedBy { get; set; }
    }
}
