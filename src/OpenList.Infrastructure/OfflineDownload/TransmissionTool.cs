using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.OfflineDownload;

/// <summary>
/// Transmission offline-download adapter. Uses the Transmission RPC session
/// (default http://host:9091/transmission/rpc). Mirrors OpenList Go
/// <c>pkg/task/offline_download/transmission.go</c>.
/// </summary>
public sealed class TransmissionTool : IOfflineDownloadTool
{
    public string Name => "transmission";
    public bool Enabled => _enabled;

    private readonly HttpClient _http;
    private readonly bool _enabled;
    private readonly string _rpcUrl;
    private readonly string _user;
    private readonly string _pass;
    private string? _session;
    private int _id;

    public TransmissionTool(HttpClient http, string uri, string user, string pass)
    {
        _http = http;
        _enabled = !string.IsNullOrWhiteSpace(uri);
        _rpcUrl = string.IsNullOrWhiteSpace(uri) ? "http://localhost:9091/transmission/rpc" : uri;
        _user = user ?? "";
        _pass = pass ?? "";
    }

    public async Task<Resp<string>> AddDownloadAsync(string url, string destPath, CancellationToken ct = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["filename"] = url,
            ["download-dir"] = destPath,
        };
        var (ok, result, err) = await CallAsync("torrent-add", args, ct);
        if (!ok) return Resp<string>.Fail(err ?? "transmission error", RespCodes.InternalError);

        // torrent-add returns either { "torrent-added": { id, hashString } } or
        // { "torrent-duplicate": { id, hashString } }.
        if (result.TryGetProperty("torrent-added", out var added) ||
            result.TryGetProperty("torrent-duplicate", out added))
        {
            var hash = added.TryGetProperty("hashString", out var h) && h.ValueKind == JsonValueKind.String
                ? h.GetString() : null;
            var id = added.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number
                ? i.GetInt64() : 0;
            return Resp<string>.Ok(hash ?? ("transmission-" + id));
        }
        return Resp<string>.Fail("transmission: no torrent id returned", RespCodes.InternalError);
    }

    public async Task<Resp> CancelAsync(string taskId, CancellationToken ct = default)
    {
        // taskId is the hashString; Transmission wants ids. Look it up first.
        var lookupArgs = new Dictionary<string, object?>
        {
            ["ids"] = new[] { taskId },
            ["fields"] = new[] { "id" },
        };
        var (lookupOk, lookupResult, lookupErr) = await CallAsync("torrent-get", lookupArgs, ct);
        if (!lookupOk) return Resp.Fail(lookupErr ?? "transmission error", RespCodes.InternalError);
        if (!lookupResult.TryGetProperty("torrents", out var torrents) ||
            torrents.ValueKind != JsonValueKind.Array ||
            !torrents.EnumerateArray().MoveNext())
            return Resp.Ok(); // already gone
        var id = torrents.EnumerateArray().First().GetProperty("id").GetInt64();

        var removeArgs = new Dictionary<string, object?>
        {
            ["ids"] = new[] { id },
            ["delete-local-data"] = false,
        };
        var (ok, _, err) = await CallAsync("torrent-remove", removeArgs, ct);
        return ok ? Resp.Ok() : Resp.Fail(err ?? "transmission error", RespCodes.InternalError);
    }

    public async Task<Resp<OfflineDownloadStatus>> GetStatusAsync(string taskId, CancellationToken ct = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["ids"] = new[] { taskId },
            ["fields"] = new[] { "status", "totalSize", "haveValid", "errorString", "error" },
        };
        var (ok, result, err) = await CallAsync("torrent-get", args, ct);
        if (!ok) return Resp<OfflineDownloadStatus>.Fail(err ?? "transmission error", RespCodes.InternalError);

        if (!result.TryGetProperty("torrents", out var torrents) ||
            torrents.ValueKind != JsonValueKind.Array ||
            !torrents.EnumerateArray().MoveNext())
            return Resp<OfflineDownloadStatus>.Ok(new OfflineDownloadStatus { State = "unknown" });

        var t = torrents.EnumerateArray().First();
        var st = new OfflineDownloadStatus
        {
            Total = t.TryGetProperty("totalSize", out var ts) && ts.ValueKind == JsonValueKind.Number ? ts.GetInt64() : 0,
            Downloaded = t.TryGetProperty("haveValid", out var hv) && hv.ValueKind == JsonValueKind.Number ? hv.GetInt64() : 0,
        };
        // Transmission status codes: 0=stopped,1=check-wait,2=checking,3=dl-wait,4=downloading,5=seed-wait,6=seeding
        if (t.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Number)
        {
            st.State = s.GetInt32() switch
            {
                0 => "paused",
                4 => "running",
                5 or 6 => "complete",
                _ => "pending",
            };
        }
        if (t.TryGetProperty("errorString", out var es) && es.ValueKind == JsonValueKind.String)
        {
            var msg = es.GetString();
            if (!string.IsNullOrEmpty(msg)) st.Error = msg;
        }
        return Resp<OfflineDownloadStatus>.Ok(st);
    }

    private async Task<(bool Ok, JsonElement Result, string? Error)> CallAsync(
        string method, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        await EnsureSessionAsync(ct);
        var body = JsonSerializer.Serialize(new
        {
            method,
            @arguments,
            tag = Interlocked.Increment(ref _id),
        });
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var content = new StringContent(body, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var resp = await _http.PostAsync(_rpcUrl, content, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                // "409 Conflict" — re-read X-Transmission-Session-Id and retry.
                if (resp.Headers.TryGetValues("X-Transmission-Session-Id", out var vs))
                    _session = vs.FirstOrDefault();
                continue;
            }
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return (false, default, $"transmission http {resp.StatusCode}: {text}");
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("result", out var result) &&
                result.GetString() != "success")
                return (false, default, "transmission: " + result.GetString());
            return (true, doc.RootElement.GetProperty("arguments").Clone(), null);
        }
        return (false, default, "transmission: session handshake failed");
    }

    private async Task EnsureSessionAsync(CancellationToken ct)
    {
        if (_session is not null) return;
        using var resp = await _http.GetAsync(_rpcUrl, ct);
        if (resp.Headers.TryGetValues("X-Transmission-Session-Id", out var vs))
            _session = vs.FirstOrDefault();
        // 409 is expected on the first probe; the header is what we care about.
    }
}
