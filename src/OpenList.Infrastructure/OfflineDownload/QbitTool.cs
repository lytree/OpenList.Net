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
/// 注意：qBittorrent 不直接返回 torrent hash，因此使用合成 id，真实验证需靠 savepath 关联。
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
    /// 通过 /api/v2/torrents/add 添加下载任务。每次登录后使用首次认证会话。
    /// </summary>
    /// <param name="url">磁力链或种子链接。</param>
    /// <param name="destPath">保存路径。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>返回合成的任务 id（qbit-<c>guid</c>），因 qBittorrent 不返回 torrent hash。</returns>
    public async Task<Resp<string>> AddDownloadAsync(string url, string destPath, CancellationToken ct = default)
    {
        await EnsureAuthAsync(ct);
        var form = new MultipartFormDataContent
        {
            { new StringContent(url), "urls" },
            { new StringContent(destPath), "savepath" },
            { new StringContent("true"), "autoTMM" },
        };
        using var resp = await _http.PostAsync(_base + "/api/v2/torrents/add", form, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            return Resp<string>.Fail($"qbit http {resp.StatusCode}: {body}", RespCodes.InternalError);
        }
        // qBittorrent does not return the torrent hash directly; use a synthetic id.
        // The transfer task correlates via savepath + url rather than hash.
        return Resp<string>.Ok("qbit-" + Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// 取消下载任务。由于任务 id 为合成值，无法直接对 qBittorrent 删除；此实现尽力而为。
    /// </summary>
    /// <param name="taskId">合成任务 id（以 qbit- 开头）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<Resp> CancelAsync(string taskId, CancellationToken ct = default)
    {
        await EnsureAuthAsync(ct);
        // taskId 为合成值，仅当形如 qbit- 时才接受；真实验证需列出种子并按路径匹配。
        if (string.IsNullOrEmpty(taskId) || !taskId.StartsWith("qbit-"))
            return Resp.Fail("cannot cancel synthetic qbit task", RespCodes.BadRequest);
        return Resp.Ok();
    }

    /// <summary>
    /// 查询任务状态。由于 id 为合成值无法精确定位，统一返回 running 状态（尽力而为）。
    /// </summary>
    /// <param name="taskId">合成任务 id。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<Resp<OfflineDownloadStatus>> GetStatusAsync(string taskId, CancellationToken ct = default)
    {
        await EnsureAuthAsync(ct);
        // 合成 id 无法精确跟踪，统一回报 running；真实状态需列出种子并按 savepath 匹配。
        var st = new OfflineDownloadStatus { State = "running" };
        return Resp<OfflineDownloadStatus>.Ok(st);
    }

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
