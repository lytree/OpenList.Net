using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Web;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.OfflineDownload;

/// <summary>
/// qBittorrent 离线下载适配器：使用 qBittorrent Web API v2（默认 http://host:8080/）。
/// 对应 Go 参考实现 <c>pkg/task/offline_download/qbittorrent.go</c>。
/// <para>
/// qBittorrent 的 <c>torrents/add</c> **不返回 torrent hash**，且 <c>torrents/info</c>
/// 没有 savepath 过滤参数。因此添加后用「本次任务唯一的下载目录」在 info 全表里
/// 反查 hash（<c>save_path</c> 精确匹配 + <c>added_on</c> 取最新），后续状态/取消
/// 都按这个真实 hash 操作。此前 GetStatus 恒返回 running，任务永远轮询不完。
/// </para>
/// </summary>
public sealed class QbitTool : IOfflineDownloadTool
{
    /// <inheritdoc/>
    public string Name => "qbit";
    /// <inheritdoc/>
    public bool Enabled => _enabled;

    private readonly HttpClient _http;
    private readonly bool _enabled;
    private readonly string _base;
    private readonly string _user;
    private readonly string _pass;
    private bool _authed;
    /// <summary>添加后反查到的真实 torrent hash（取消/查询都靠它）。</summary>
    private string? _hash;

    /// <summary>
    /// 创建 qBittorrent 适配器实例。
    /// </summary>
    /// <param name="http">共享的 <see cref="HttpClient"/> 实例。</param>
    /// <param name="url">qBittorrent Web 地址（为空则表示禁用）。</param>
    /// <param name="user">登录用户名。</param>
    /// <param name="pass">登录密码。</param>
    public QbitTool(HttpClient http, string url, string user, string pass)
    {
        _http = http;
        _enabled = !string.IsNullOrWhiteSpace(url);
        _base = string.IsNullOrWhiteSpace(url) ? "http://localhost:8080" : url.TrimEnd('/');
        _user = user ?? "";
        _pass = pass ?? "";
    }

    /// <summary>
    /// 通过 /api/v2/torrents/add 添加下载任务，并反查其真实 hash 作为任务 id。
    /// </summary>
    /// <param name="url">磁力链或种子链接。</param>
    /// <param name="downloadDir">qBittorrent 本机可写的下载目录（不是 VFS 路径）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功时返回真实 torrent hash（反查失败时退化为合成 id）。</returns>
    public async Task<Resp<string>> AddDownloadAsync(string url, string downloadDir, CancellationToken ct = default)
    {
        await EnsureAuthAsync(ct);
        // autoTMM（自动种子管理）必须关掉：开着的话 qBittorrent 会按分类/默认路径覆盖
        // 我们传入的 savepath，反查 hash 时就匹配不到唯一目录。
        // paused 必须为 false：添加时若暂停，状态会停在 pausedDL，轮询永远等不到 complete。
        var form = new MultipartFormDataContent
        {
            { new StringContent(url), "urls" },
            { new StringContent(downloadDir), "savepath" },
            { new StringContent("false"), "autoTMM" },
            { new StringContent("false"), "paused" },
        };
        using var resp = await _http.PostAsync(_base + "/api/v2/torrents/add", form, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            return Resp<string>.Fail($"qbit http {resp.StatusCode}: {body}", RespCodes.InternalError);
        }

        // torrents/add 只回 "Ok."，没有 hash。反查：按 save_path 精确匹配（本次任务的
        // 下载目录是唯一的，所以不会误命中别人的任务），多条命中取 added_on 最大的。
        _hash = await ResolveHashAsync(downloadDir, ct);
        return Resp<string>.Ok(_hash ?? ("qbit-" + Guid.NewGuid().ToString("N")));
    }

    /// <summary>
    /// 取消下载任务：有真实 hash 时删除种子并连带删除临时文件。
    /// </summary>
    /// <param name="taskId">真实 torrent hash（合成 id 无法取消）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<Resp> CancelAsync(string taskId, CancellationToken ct = default)
    {
        await EnsureAuthAsync(ct);
        var hash = _hash ?? (taskId.StartsWith("qbit-", StringComparison.Ordinal) ? null : taskId);
        if (string.IsNullOrEmpty(hash))
            return Resp.Fail("cannot cancel synthetic qbit task", RespCodes.BadRequest);

        // deleteFiles=true：临时目录里的产物要一并清掉，否则下次 transfer 会读到半成品。
        var url = $"{_base}/api/v2/torrents/delete?hashes={Uri.EscapeDataString(hash)}&deleteFiles=true";
        using var resp = await _http.PostAsync(url, null, ct);
        return resp.IsSuccessStatusCode
            ? Resp.Ok()
            : Resp.Fail($"qbit http {resp.StatusCode}", RespCodes.InternalError);
    }

    /// <summary>
    /// 查询任务状态：按 hash 精确取条目，映射状态并带回产物路径。
    /// </summary>
    /// <param name="taskId">真实 torrent hash。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<Resp<OfflineDownloadStatus>> GetStatusAsync(string taskId, CancellationToken ct = default)
    {
        await EnsureAuthAsync(ct);
        var hash = _hash ?? (taskId.StartsWith("qbit-", StringComparison.Ordinal) ? null : taskId);
        if (string.IsNullOrEmpty(hash))
            return Resp<OfflineDownloadStatus>.Ok(new OfflineDownloadStatus { State = "unknown" });

        var url = $"{_base}/api/v2/torrents/info?hashes={Uri.EscapeDataString(hash)}";
        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
            return Resp<OfflineDownloadStatus>.Fail($"qbit http {resp.StatusCode}", RespCodes.InternalError);

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return Resp<OfflineDownloadStatus>.Ok(new OfflineDownloadStatus { State = "unknown" });

        var t = doc.RootElement.EnumerateArray().First();
        var st = new OfflineDownloadStatus
        {
            Total = ReadLong(t, "size"),
            Downloaded = ReadLong(t, "completed"),
            State = MapState(t.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() ?? ""
                : ""),
        };
        st.Error = t.TryGetProperty("state", out var st2) && st2.ValueKind == JsonValueKind.String
                   && (st2.GetString() ?? "").Contains("error", StringComparison.OrdinalIgnoreCase)
            ? "qbit: " + st2.GetString()
            : null;

        // 产物路径：优先 content_path（BT 多文件时为种子顶层目录，transfer 会递归展开）；
        // 单文件任务没有 content_path 时用 save_path + name 兜底。
        if (t.TryGetProperty("content_path", out var cp) && cp.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(cp.GetString()))
            st.Files.Add(cp.GetString()!);
        else if (t.TryGetProperty("save_path", out var sp) && sp.ValueKind == JsonValueKind.String
                 && t.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
            st.Files.Add(Path.Combine(sp.GetString()!, nm.GetString()!));

        return Resp<OfflineDownloadStatus>.Ok(st);
    }

    /// <summary>
    /// 在 <c>torrents/info</c> 全表里按 <c>save_path</c> 精确匹配，取 <c>added_on</c> 最新的一条的 hash。
    /// </summary>
    private async Task<string?> ResolveHashAsync(string downloadDir, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(_base + "/api/v2/torrents/info", ct);
        if (!resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        string? bestHash = null;
        long bestAdded = long.MinValue;
        foreach (var t in doc.RootElement.EnumerateArray())
        {
            if (!t.TryGetProperty("save_path", out var sp) || sp.ValueKind != JsonValueKind.String) continue;
            // qBittorrent 返回的 save_path 末尾带斜杠，比较时统一去掉。
            var savePath = sp.GetString()!.TrimEnd('/', '\\');
            if (!string.Equals(savePath, downloadDir.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase)) continue;
            var added = ReadLong(t, "added_on");
            if (added <= bestAdded) continue;
            if (!t.TryGetProperty("hash", out var h) || h.ValueKind != JsonValueKind.String) continue;
            bestAdded = added;
            bestHash = h.GetString();
        }
        return bestHash;
    }

    /// <summary>读取一个数值字段（qBittorrent 混用数字与字符串，两种都要吃）。</summary>
    private static long ReadLong(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var n) ? n : 0,
            JsonValueKind.String => long.TryParse(v.GetString(), out var n) ? n : 0,
            _ => 0,
        };
    }

    /// <summary>
    /// 把 qBittorrent 的 <c>state</c> 映射为统一状态
    /// （pending / running / paused / complete / error / canceled）。
    /// </summary>
    private static string MapState(string s) => s switch
    {
        // 完成态：做种 / 已暂停的做种 / 等待做种 / 校验完成
        "uploading" or "stalledUP" or "queuedUP" or "pausedUP" or "forcedUP" or "checkingUP" => "complete",
        // 进行态
        "downloading" or "stalledDL" or "metaDL" or "forcedDL" or "allocating" or "checkingDL"
            or "checkingResumeData" or "moving" => "running",
        "pausedDL" or "queuedDL" => "paused",
        "error" or "missingFiles" => "error",
        _ => "pending",
    };

    /// <summary>
    /// 确保已通过 /api/v2/auth/login 建立会话（仅首次需要）。
    /// </summary>
    private async Task EnsureAuthAsync(CancellationToken ct)
    {
        if (_authed) return;
        var form = new Dictionary<string, string>
        {
            ["username"] = _user,
            ["password"] = _pass,
        };
        using var content = new FormUrlEncodedContent(form);
        using var resp = await _http.PostAsync(_base + "/api/v2/auth/login", content, ct);
        resp.EnsureSuccessStatusCode();
        _authed = true;
    }
}
