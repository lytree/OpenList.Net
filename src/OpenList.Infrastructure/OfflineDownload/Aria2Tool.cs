using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.OfflineDownload;

/// <summary>
/// aria2 offline-download adapter. Talks to aria2's JSON-RPC 2.0 endpoint
/// (default http://host:6800/jsonrpc). Mirrors OpenList Go
/// <c>pkg/task/offline_download/aria2.go</c>.
/// </summary>
public sealed class Aria2Tool : IOfflineDownloadTool
{
    public string Name => "aria2";
    public bool Enabled => _enabled;

    private readonly HttpClient _http;
    private readonly bool _enabled;
    private readonly string _rpcUrl;
    private readonly string _secret;
    private int _id;

    public Aria2Tool(HttpClient http, string uri, string secret)
    {
        _http = http;
        _enabled = !string.IsNullOrWhiteSpace(uri);
        _rpcUrl = string.IsNullOrWhiteSpace(uri) ? "http://localhost:6800/jsonrpc" : uri.TrimEnd('/');
        _secret = secret ?? "";
    }

    public async Task<Resp<string>> AddDownloadAsync(string url, string destPath, CancellationToken ct = default)
    {
        // aria2.addUri params: [secret, [url], {dir: ...}]
        var args = new List<object>();
        if (!string.IsNullOrEmpty(_secret)) args.Add("token:" + _secret);
        args.Add(new[] { url });
        args.Add(new Dictionary<string, string> { ["dir"] = destPath });

        var (ok, result, err) = await CallAsync("aria2.addUri", args, ct);
        if (!ok) return Resp<string>.Fail(err ?? "aria2 error", RespCodes.InternalError);
        return Resp<string>.Ok(result.ToString()!);
    }

    public async Task<Resp> CancelAsync(string taskId, CancellationToken ct = default)
    {
        var args = new List<object>();
        if (!string.IsNullOrEmpty(_secret)) args.Add("token:" + _secret);
        args.Add(taskId);
        var (ok, _, err) = await CallAsync("aria2.remove", args, ct);
        return ok ? Resp.Ok() : Resp.Fail(err ?? "aria2 error", RespCodes.InternalError);
    }

    public async Task<Resp<OfflineDownloadStatus>> GetStatusAsync(string taskId, CancellationToken ct = default)
    {
        var args = new List<object>();
        if (!string.IsNullOrEmpty(_secret)) args.Add("token:" + _secret);
        args.Add(taskId);
        args.Add(new[] { "gid", "status", "totalLength", "completedLength", "errorCode", "errorMessage" });

        var (ok, result, err) = await CallAsync("aria2.tellStatus", args, ct);
        if (!ok) return Resp<OfflineDownloadStatus>.Fail(err ?? "aria2 error", RespCodes.InternalError);

        var st = new OfflineDownloadStatus();
        foreach (var p in result.EnumerateObject())
        {
            switch (p.Name)
            {
                case "status":
                    st.State = MapState(p.Value.GetString() ?? "");
                    break;
                case "totalLength":
                    st.Total = long.TryParse(p.Value.ToString(), out var t) ? t : 0;
                    break;
                case "completedLength":
                    st.Downloaded = long.TryParse(p.Value.ToString(), out var d) ? d : 0;
                    break;
                case "errorMessage":
                case "errorCode":
                    if (p.Value.ValueKind == JsonValueKind.String)
                    {
                        var s = p.Value.GetString();
                        if (!string.IsNullOrEmpty(s)) st.Error = s;
                    }
                    break;
            }
        }
        return Resp<OfflineDownloadStatus>.Ok(st);
    }

    private static string MapState(string s) => s switch
    {
        "active" or "waiting" => "running",
        "paused" => "paused",
        "complete" => "complete",
        "removed" => "canceled",
        "error" => "error",
        _ => "pending",
    };

    private async Task<(bool Ok, JsonElement Result, string? Error)> CallAsync(
        string method, IReadOnlyList<object> args, CancellationToken ct)
    {
        var req = new
        {
            jsonrpc = "2.0",
            id = Interlocked.Increment(ref _id),
            method,
            @params = args,
        };
        using var content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        HttpResponseMessage resp;
        try { resp = await _http.PostAsync(_rpcUrl, content, ct); }
        catch (Exception ex) { return (false, default, "aria2 unreachable: " + ex.Message); }
        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return (false, default, $"aria2 http {resp.StatusCode}: {body}");
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
                return (false, default, err.GetProperty("message").GetString() ?? "aria2 error");
            return (true, doc.RootElement.GetProperty("result").Clone(), null);
        }
    }
}
