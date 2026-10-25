using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Drivers.Cloud.DiskApi;

namespace OpenList.Drivers.Cloud.Pan115;

/// <summary>
/// 115 网盘驱动（Cookie 认证）。使用 Web API（<c>webapi.115.com</c>）：
/// 浏览、直链下载、建目录、删除、移动、改名。上传不支持。
/// <para>
/// 115 没有官方开放 API，Cookie（UID/CID/SEID/KID）会随会话过期，需要在挂载配置里更新。
/// 定位与 PikPak 一致：作为"云下载来源"，用既有复制/下载任务把内容转存到目标存储。
/// </para>
/// </summary>
public sealed class Pan115Driver : CloudDiskDriverBase
{
    /// <summary>Web API 根地址。</summary>
    internal const string ApiUrl = "https://webapi.115.com";

    /// <inheritdoc />
    public override string DriverName => "115";

    /// <inheritdoc />
    public override bool NoUpload => true;

    /// <inheritdoc />
    public override string DefaultRoot => "0";

    /// <inheritdoc />
    public override string Alert =>
        "warning|115 无官方开放 API，依赖网页端 Cookie（UID/CID/SEID/KID）；Cookie 过期后需重新填写。";

    /// <inheritdoc />
    public override IEnumerable<DriverItemAttributes> ConfigItems => ConfigSchema;

    /// <summary>配置字段清单。</summary>
    internal static readonly DriverItemAttributes[] ConfigSchema =
    [
        new() { Name = "cookie", Help = "Web cookie, e.g. UID=...; CID=...; SEID=...; KID=...", Required = true, ValueType = typeof(string) },
        new() { Name = "root_folder_id", Help = "Root folder id (0 means drive root)", ValueType = typeof(string), Default = "0" },
        new() { Name = "limit_rate", Help = "Requests per second limit (0 = unlimited)", ValueType = typeof(float), Default = 0f },
    ];

    /// <summary>附加配置。</summary>
    public Pan115Addition Addition { get; private set; } = new();

    /// <inheritdoc />
    protected override object AdditionObject => Addition;

    /// <inheritdoc />
    protected override ICloudDiskApi CreateApi() => new Pan115Api(this);

    /// <summary>挂载根目录 cid。</summary>
    internal string RootFolderId => string.IsNullOrWhiteSpace(Addition.RootFolderId) ? "0" : Addition.RootFolderId!;

    /// <inheritdoc />
    public override Task InitAsync(DriverConfig config, CancellationToken ct = default)
    {
        Addition = config.GetAddition<Pan115Addition>();
        if (Addition.LimitRate > 0) ConfigureRateLimit(Addition.LimitRate);
        if (string.IsNullOrWhiteSpace(Addition.Cookie))
            throw new InvalidOperationException("cookie is required");

        AttachFileSystem();
        return Task.CompletedTask;
    }

    /// <summary>供 API 客户端等待限流放行。</summary>
    internal Task WaitAsync(CancellationToken ct) => WaitRateLimitAsync(ct);

    /// <summary>构造带 Cookie 与 Referer 的请求（115 对来源校验较严）。</summary>
    internal HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("Cookie", Addition.Cookie);
        req.Headers.Add("Referer", "https://115.com/");
        req.Headers.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        return req;
    }

    internal static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.ToString(),
                _ => null,
            }
            : null;

    internal static long Long(JsonElement e, string name)
        => long.TryParse(Str(e, name), out var n) ? n : 0;
}

/// <summary>115 Web API 客户端。</summary>
internal sealed class Pan115Api : ICloudDiskApi
{
    private readonly Pan115Driver _driver;

    /// <summary>fid → pickcode 映射：下载直链需要 pickcode，而节点 id 必须是 fid（删除/移动用）。</summary>
    private readonly ConcurrentDictionary<string, string> _pickCodes = new(StringComparer.Ordinal);

    internal Pan115Api(Pan115Driver driver) => _driver = driver;

    public string RootId => _driver.RootFolderId;

    public HttpClient HttpClient => CloudHttpClient.Shared;

    public async Task<IReadOnlyList<CloudDiskNode>> ListAsync(string parentId, CancellationToken ct)
    {
        var result = new List<CloudDiskNode>();
        for (var offset = 0; offset < 100_000; offset += 500)
        {
            await _driverRateLimitAsync(ct).ConfigureAwait(false);
            var url = $"{Pan115Driver.ApiUrl}/files?aid=1&cid={Uri.EscapeDataString(parentId)}&offset={offset}" +
                      "&show_dir=1&limit=500&o=user_ptime&asc=0&snap=0";
            using var req = _driver.CreateRequest(HttpMethod.Get, url);
            using var resp = await HttpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) break;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) break;

            var batch = data.EnumerateArray().ToList();
            foreach (var f in batch) result.Add(ToNode(f, parentId));
            if (batch.Count < 500) break;
        }
        return result;
    }

    public Task<CloudDiskNode?> GetAsync(string id, CancellationToken ct)
    {
        // 115 没有按 fid 取单文件的公开接口，返回列表缓存中的最小信息即可（大小/时间以列表为准）。
        return Task.FromResult<CloudDiskNode?>(new CloudDiskNode { Id = id, ParentId = string.Empty });
    }

    public async Task<string?> GetDownloadUrlAsync(string id, CancellationToken ct)
    {
        if (!_pickCodes.TryGetValue(id, out var pickCode) || string.IsNullOrEmpty(pickCode)) return null;

        await _driverRateLimitAsync(ct).ConfigureAwait(false);
        using var req = _driver.CreateRequest(HttpMethod.Get,
            $"{Pan115Driver.ApiUrl}/files/download?pickcode={Uri.EscapeDataString(pickCode)}");
        using var resp = await HttpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return Pan115Driver.Str(doc.RootElement, "file_url");
    }

    public async Task<CloudDiskNode> MakeDirAsync(string parentId, string name, CancellationToken ct)
    {
        await _driverRateLimitAsync(ct).ConfigureAwait(false);
        using var req = _driver.CreateRequest(HttpMethod.Post, $"{Pan115Driver.ApiUrl}/files/add");
        req.Content = Form(new Dictionary<string, string> { ["pid"] = parentId, ["cname"] = name });
        using var resp = await HttpClient.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new IOException($"115 mkdir failed: {text}");

        using var doc = JsonDocument.Parse(text);
        var cid = doc.RootElement.TryGetProperty("cid", out var c) ? c.ToString() : string.Empty;
        return new CloudDiskNode { Id = cid, ParentId = parentId, Name = name, IsFolder = true };
    }

    public async Task DeleteAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        await _driverRateLimitAsync(ct).ConfigureAwait(false);
        using var req = _driver.CreateRequest(HttpMethod.Post, $"{Pan115Driver.ApiUrl}/rb/delete");
        var form = new Dictionary<string, string>();
        for (var i = 0; i < ids.Count; i++) form[$"fid[{i}]"] = ids[i];
        req.Content = Form(form);
        using var resp = await HttpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new IOException($"115 delete failed: {await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)}");
    }

    public async Task MoveAsync(string id, string? newParentId, string? newName, CancellationToken ct)
    {
        await _driverRateLimitAsync(ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(newParentId))
        {
            using var moveReq = _driver.CreateRequest(HttpMethod.Post, $"{Pan115Driver.ApiUrl}/files/move");
            moveReq.Content = Form(new Dictionary<string, string> { ["pid"] = newParentId, ["fid[0]"] = id });
            using var moveResp = await HttpClient.SendAsync(moveReq, ct).ConfigureAwait(false);
            if (!moveResp.IsSuccessStatusCode)
                throw new IOException($"115 move failed: {await moveResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)}");
        }

        if (!string.IsNullOrEmpty(newName))
        {
            using var renameReq = _driver.CreateRequest(HttpMethod.Post, $"{Pan115Driver.ApiUrl}/files/batch_rename");
            renameReq.Content = Form(new Dictionary<string, string> { [$"files_new_name[{id}]"] = newName });
            using var renameResp = await HttpClient.SendAsync(renameReq, ct).ConfigureAwait(false);
            if (!renameResp.IsSuccessStatusCode)
                throw new IOException($"115 rename failed: {await renameResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)}");
        }
    }

    private Task _driverRateLimitAsync(CancellationToken ct) => _driver.WaitAsync(ct);

    private static FormUrlEncodedContent Form(Dictionary<string, string> values)
        => new(values);

    /// <summary>把 115 文件 JSON 映射为节点，并记录 pickcode。</summary>
    private CloudDiskNode ToNode(JsonElement e, string fallbackParent)
    {
        var fid = Pan115Driver.Str(e, "fid") ?? string.Empty;
        var pickCode = Pan115Driver.Str(e, "pc");
        if (!string.IsNullOrEmpty(fid) && !string.IsNullOrEmpty(pickCode)) _pickCodes[fid] = pickCode;

        return new CloudDiskNode
        {
            Id = fid,
            ParentId = Pan115Driver.Str(e, "cid") ?? fallbackParent,
            Name = Pan115Driver.Str(e, "n") ?? string.Empty,
            IsFolder = Pan115Driver.Str(e, "fc") == "0",
            Size = Pan115Driver.Long(e, "s"),
            Modified = ParseUnix(Pan115Driver.Long(e, "te")),
            Created = ParseUnix(Pan115Driver.Long(e, "tp")),
        };
    }

    private static DateTimeOffset ParseUnix(long seconds)
        => seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : DateTimeOffset.MinValue;
}

/// <summary>115 附加配置。</summary>
public sealed class Pan115Addition
{
    /// <summary>网页端 Cookie。</summary>
    [JsonPropertyName("cookie")]
    public string Cookie { get; set; } = string.Empty;

    /// <summary>挂载根目录 cid，默认 0（网盘根）。</summary>
    [JsonPropertyName("root_folder_id")]
    public string? RootFolderId { get; set; }

    /// <summary>每秒请求数上限，0 表示不限。</summary>
    [JsonPropertyName("limit_rate")]
    public float LimitRate { get; set; }
}
