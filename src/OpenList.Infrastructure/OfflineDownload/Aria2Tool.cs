using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.OfflineDownload;

/// <summary>
/// aria2 离线下载适配器：通过 aria2 的 JSON-RPC 2.0 接口（默认 http://host:6800/jsonrpc）
/// 完成任务的添加、取消与状态查询。对应 Go 参考实现 <c>pkg/task/offline_download/aria2.go</c>。
/// </summary>
public sealed class Aria2Tool : IOfflineDownloadTool
{
    /// <inheritdoc/>
    public string Name => "aria2";
    /// <inheritdoc/>
    public bool Enabled => _enabled;

    private readonly HttpClient _http;
    private readonly bool _enabled;
    private readonly string _rpcUrl;
    private readonly string _secret;
    private int _id;

    /// <summary>
    /// 创建 aria2 适配器实例。
    /// </summary>
    /// <param name="http">共享的 <see cref="HttpClient"/> 实例。</param>
    /// <param name="uri">aria2 JSON-RPC 地址（为空则表示禁用）。</param>
    /// <param name="secret">RPC 密钥（可选，非空时以 <c>token:</c> 前缀随请求发送）。</param>
    public Aria2Tool(HttpClient http, string uri, string secret)
    {
        _http = http;
        _enabled = !string.IsNullOrWhiteSpace(uri);
        _rpcUrl = string.IsNullOrWhiteSpace(uri) ? "http://localhost:6800/jsonrpc" : uri.TrimEnd('/');
        _secret = secret ?? "";
    }

    /// <summary>
    /// 通过 aria2.addUri 添加下载任务。
    /// </summary>
    /// <param name="url">待下载的链接。</param>
    /// <param name="destPath">保存目录。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功时返回 aria2 分配的 GID；失败返回错误信息。</returns>
    public async Task<Resp<string>> AddDownloadAsync(string url, string destPath, CancellationToken ct = default)
    {
        // aria2.addUri 参数：[secret, [url], {dir: ...}]
        var args = new List<object>();
        if (!string.IsNullOrEmpty(_secret)) args.Add("token:" + _secret);
        args.Add(new[] { url });
        args.Add(new Dictionary<string, string> { ["dir"] = destPath });

        var (ok, result, err) = await CallAsync("aria2.addUri", args, ct);
        if (!ok) return Resp<string>.Fail(err ?? "aria2 error", RespCodes.InternalError);
        return Resp<string>.Ok(result.ToString()!);
    }

    /// <summary>
    /// 通过 aria2.remove 按 GID 取消下载任务。
    /// </summary>
    /// <param name="taskId">aria2 的 GID（添加任务时的返回值）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<Resp> CancelAsync(string taskId, CancellationToken ct = default)
    {
        var args = new List<object>();
        if (!string.IsNullOrEmpty(_secret)) args.Add("token:" + _secret);
        args.Add(taskId);
        var (ok, _, err) = await CallAsync("aria2.remove", args, ct);
        return ok ? Resp.Ok() : Resp.Fail(err ?? "aria2 error", RespCodes.InternalError);
    }

    /// <summary>
    /// 通过 aria2.tellStatus 查询任务状态，并将 aria2 的状态码映射为统一模型。
    /// </summary>
    /// <param name="taskId">aria2 的 GID。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>归档后的下载状态（含已下载/总量/错误信息）。</returns>
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

    /// <summary>
    /// 把 aria2 的状态字符串映射为统一状态模型（running/paused/complete/canceled/error/pending）。
    /// </summary>
    private static string MapState(string s) => s switch
    {
        "active" or "waiting" => "running",
        "paused" => "paused",
        "complete" => "complete",
        "removed" => "canceled",
        "error" => "error",
        _ => "pending",
    };

    /// <summary>
    /// 发送一条 aria2 JSON-RPC 2.0 请求并解析响应，统一处理网络异常、HTTP 错误码与 RPC 错误。
    /// </summary>
    /// <param name="method">RPC 方法名（如 aria2.addUri）。</param>
    /// <param name="args">RPC 参数列表。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功标记、result 元素与错误信息三元组（失败时 Result 为 default）。</returns>
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
