using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.OfflineDownload;

/// <summary>
/// Transmission 离线下载适配器：使用 Transmission RPC 会话（默认 http://host:9091/transmission/rpc）。
/// 对应 Go 参考实现 <c>pkg/task/offline_download/transmission.go</c>。
/// </summary>
public sealed class TransmissionTool : IOfflineDownloadTool
{
    /// <inheritdoc/>
    public string Name => "transmission";
    /// <inheritdoc/>
    public bool Enabled => _enabled;

    private readonly HttpClient _http;
    private readonly bool _enabled;
    private readonly string _rpcUrl;
    private readonly string _user;
    private readonly string _pass;
    private string? _session;
    private int _id;

    /// <summary>
    /// 创建 Transmission 适配器实例。
    /// </summary>
    /// <param name="http">共享的 <see cref="HttpClient"/> 实例。</param>
    /// <param name="uri">Transmission RPC 地址（为空则表示禁用）。</param>
    /// <param name="user">登录用户名（当前版本 RPC 调用未主动使用基本认证）。</param>
    /// <param name="pass">登录密码。</param>
    public TransmissionTool(HttpClient http, string uri, string user, string pass)
    {
        _http = http;
        _enabled = !string.IsNullOrWhiteSpace(uri);
        _rpcUrl = string.IsNullOrWhiteSpace(uri) ? "http://localhost:9091/transmission/rpc" : uri;
        _user = user ?? "";
        _pass = pass ?? "";
    }

    /// <summary>
    /// 通过 torrent-add 添加下载任务，并返回任务的 hashString（缺失时退化为 transmission-<c>id</c>）。
    /// </summary>
    /// <param name="url">磁力链或种子链接。</param>
    /// <param name="destPath">下载保存目录。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回任务标识（hashString 或合成 id）。</returns>
    public async Task<Resp<string>> AddDownloadAsync(string url, string destPath, CancellationToken ct = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["filename"] = url,
            ["download-dir"] = destPath,
        };
        var (ok, result, err) = await CallAsync("torrent-add", args, ct);
        if (!ok) return Resp<string>.Fail(err ?? "transmission error", RespCodes.InternalError);

        // torrent-add 返回两种结构之一：{ "torrent-added": { id, hashString } } 或
        // { "torrent-duplicate": { id, hashString } }。
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

    /// <summary>
    /// 取消下载任务：先按 hashString 通过 torrent-get 查询真正的 id，再以 torrent-remove 删除。
    /// </summary>
    /// <param name="taskId">任务 hashString。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<Resp> CancelAsync(string taskId, CancellationToken ct = default)
    {
        // taskId 为 hashString，而 Transmission 删除需要内部 id，先查再删。
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

    /// <summary>
    /// 查询任务状态（总量/已下载/状态码/错误信息），并将 Transmission 状态码映射为统一状态。
    /// </summary>
    /// <param name="taskId">任务 hashString。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>归档后的下载状态。</returns>
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
        // Transmission 状态码：0=stopped,1=check-wait,2=checking,3=dl-wait,4=downloading,5=seed-wait,6=seeding
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

    /// <summary>
    /// 发送一条 Transmission RPC 请求。若收到 409 Conflict（会话过期/未建立）则读取
    /// X-Transmission-Session-Id 后重试一次，并统一处理 HTTP/RPC 错误。
    /// </summary>
    /// <param name="method">RPC 方法名（如 torrent-add）。</param>
    /// <param name="arguments">RPC 参数字典。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功标记、arguments 元素与错误信息三元组。</returns>
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
                // 收到 409 Conflict — 重新读取 X-Transmission-Session-Id 并重试。
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

    /// <summary>
    /// 通过首次 GET 响应头获取 X-Transmission-Session-Id（会话建立）。首次探测的 409 属预期。
    /// </summary>
    private async Task EnsureSessionAsync(CancellationToken ct)
    {
        if (_session is not null) return;
        using var resp = await _http.GetAsync(_rpcUrl, ct);
        if (resp.Headers.TryGetValues("X-Transmission-Session-Id", out var vs))
            _session = vs.FirstOrDefault();
        // 首次探测收到 409 属预期，我们关心的只是其中的会话 id 响应头。
    }
}
