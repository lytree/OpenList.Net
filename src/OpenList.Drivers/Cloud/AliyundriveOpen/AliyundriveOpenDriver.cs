using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using Zio;

namespace OpenList.Drivers.Cloud.AliyundriveOpen;

/// <summary>
/// 阿里云盘 Open（<c>openapi.alipan.com</c>）驱动，对应 Go <c>drivers/aliyundrive_open</c>。
/// <para>
/// 已移植的能力：<c>getDriveInfo</c> 初始化（拿 drive_id / user_id）、OAuth 令牌刷新
/// （在线续期 API 与自建应用两条路径）、列目录（自动翻页）、直链与代理下载
/// （代理走 <see cref="CallbackRangeReader"/>，按阿里云用户共享回调并发配额）、
/// 新建目录 / 移动 / 改名 / 复制 / 删除，以及上传（秒传 pre_hash + 分片 + complete）。
/// </para>
/// <para>
/// 两处与 Go 的实现差异，都是有意的：
/// <list type="bullet">
/// <item>Go 的 <c>InitReference</c>（同一账号多挂载点共用 access token 与限流器）尚未移植；
/// 退化为「各挂载点各自维护 token」，限流与回调配额仍按 <c>user_id</c> 在进程内正确共享。</item>
/// <item>Go 在 Move/Rename/Copy 之后会清理目标目录的同名残留（云盘允许同名文件）；
/// 本实现不做该清理，保持文件系统的「同名唯一」语义。</item>
/// </list>
/// </para>
/// </summary>
public sealed class AliyundriveOpenDriver : CloudDriverBase, IFileSystemLinkProvider, IFileSystemUploader
{
    /// <summary>云盘 API 根地址，对齐 Go <c>API_URL</c>。</summary>
    internal const string ApiUrl = "https://openapi.alipan.com";

    /// <summary>上传分片基准大小（20 MB），对齐 Go <c>calPartSize</c> 的起始值。</summary>
    private const long BasePartSize = 20L * 1024 * 1024;

    /// <summary>秒传预处理的最小文件（小于该值不做 pre_hash），对齐 Go 的 100 KiB 阈值。</summary>
    private const long RapidUploadMinSize = 100L * 1024;

    /// <summary>令牌/限流/回调的运行期租约。</summary>
    private AliyunLimiterLease? _limiter;

    /// <summary>回调准入注册句柄（释放驱动时注销）。</summary>
    private CallbackLimiterRegistration? _callback;

    /// <summary>Zio 视图（路径 → 对象解析与缓存）。</summary>
    private AliyundriveOpenFileSystem? _fs;

    /// <summary>测试可注入的 HTTP 客户端；为空时使用进程级共享客户端。</summary>
    private HttpClient? _httpOverride;

    /// <summary>本驱动实际使用的 HTTP 客户端。</summary>
    internal HttpClient HttpClient => _httpOverride ?? CloudHttpClient.Shared;

    /// <summary>本驱动展示名，与 Go <c>driver.Config.Name</c> 一致（前端翻译键）。</summary>
    public override string DriverName => "AliyundriveOpen";

    /// <summary>默认根目录 id，对齐 Go <c>Config.DefaultRoot = "root"</c>。</summary>
    public override string DefaultRoot => "root";

    /// <summary>
    /// 配置页提示。核心 API 已移植但仍未经真实账号验证，用 info 级提示而非告警 ——
    /// 误标为 warning/danger 会让用户以为驱动不可用。
    /// </summary>
    public override string Alert =>
        "info|核心 API 已移植（列举 / 直链 / 代理下载 / 上传 / 增删改），尚未在真实账号上验证，请先小范围试挂载。";

    /// <inheritdoc />
    public override IEnumerable<DriverItemAttributes> ConfigItems => ConfigSchema;

    /// <summary>
    /// 驱动配置字段。逐项对齐 Go <c>aliyundrive_open.Addition</c> 的 json 名、类型、
    /// 默认值与候选项；字段名同时是前端 <c>src/lang/en/drivers.json</c> 的翻译键。
    /// </summary>
    internal static readonly DriverItemAttributes[] ConfigSchema =
    [
        new() { Name = "drive_type", Help = "Which drive to use", ValueType = typeof(string), Options = "default,resource,backup", Default = "resource" },
        new() { Name = "root_folder_id", Help = "Root folder id", Required = true, ValueType = typeof(string), Default = "root" },
        new() { Name = "refresh_token", Help = "OAuth refresh token", Required = true, ValueType = typeof(string) },
        new() { Name = "order_by", Help = "The default sort field", ValueType = typeof(string), Options = "name,size,updated_at,created_at" },
        new() { Name = "order_direction", Help = "The default sort direction", ValueType = typeof(string), Options = "ASC,DESC" },
        new() { Name = "use_online_api", Help = "Use the online API to refresh the token", ValueType = typeof(bool), Default = true },
        new() { Name = "alipan_type", Help = "Alipan client type", Required = true, ValueType = typeof(string), Options = "default,alipanTV", Default = "default" },
        new() { Name = "api_url_address", Help = "Online API address used to renew the token", ValueType = typeof(string), Default = "https://api.oplist.org/alicloud/renewapi" },
        new() { Name = "client_id", Help = "Keep it empty if you don't have one", ValueType = typeof(string) },
        new() { Name = "client_secret", Help = "Keep it empty if you don't have one", ValueType = typeof(string) },
        new() { Name = "remove_way", Help = "How to remove files", Required = true, ValueType = typeof(string), Options = "trash,delete" },
        new() { Name = "rapid_upload", Help = "If you enable this option, the file will be uploaded to the server first, so the progress will be incorrect", ValueType = typeof(bool) },
        new() { Name = "internal_upload", Help = "If you are using Aliyun ECS is located in Beijing, you can turn it on to boost the upload speed", ValueType = typeof(bool) },
        new() { Name = "livp_download_format", Help = "Download format for livp files", ValueType = typeof(string), Options = "jpeg,mov", Default = "jpeg" },
        new() { Name = "callback_concurrency", Help = "Maximum active proxied downloads shared by Aliyun user", ValueType = typeof(int), Default = 1 },
    ];

    /// <summary>本驱动的附加配置（由 <see cref="InitAsync"/> 从存储的 Addition JSON 解析）。</summary>
    public AliyundriveOpenAddition Addition { get; private set; } = new();

    /// <inheritdoc />
    protected override object AdditionObject => Addition;

    /// <summary>云盘 drive id（由 <c>getDriveInfo</c> 按 <c>drive_type</c> 选取）。</summary>
    public string DriveId { get; private set; } = string.Empty;

    /// <summary>阿里云 user id：限流与回调并发配额的身份键。</summary>
    public string UserId { get; private set; } = string.Empty;

    /// <summary>挂载根目录 id（配置缺失时回退 <c>root</c>）。</summary>
    internal string RootFolderId => string.IsNullOrEmpty(Addition.RootFolderId) ? "root" : Addition.RootFolderId;

    /// <summary>注入测试用 HTTP 客户端（生产路径不调用）。</summary>
    internal void UseHttpClient(HttpClient client) => _httpOverride = client;

    /// <summary>
    /// 初始化：解析配置 → 用全局限流槽位调 <c>getDriveInfo</c> 取得 <c>drive_id</c> 与
    /// <c>user_id</c> → 切换到按用户共享的限流器与回调准入 → 挂载云盘文件系统。
    /// <para>
    /// 对齐 Go <c>Init</c> 的两阶段限流策略：首个请求只能落在全局槽位（此时还不知道用户身份），
    /// 拿到 user_id 后立即换成真实用户槽位，使同账号的多个挂载点共享同一套配额。
    /// </para>
    /// </summary>
    /// <param name="config">包含 Addition JSON 的驱动配置。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="InvalidOperationException">refresh_token 缺失时抛出。</exception>
    public override async Task InitAsync(DriverConfig config, CancellationToken ct = default)
    {
        Addition = config.GetAddition<AliyundriveOpenAddition>();

        // 与 Go 一致：空值补默认，避免下游拿到空字符串。
        if (string.IsNullOrEmpty(Addition.DriveType)) Addition.DriveType = "default";
        if (string.IsNullOrEmpty(Addition.LivpDownloadFormat)) Addition.LivpDownloadFormat = "jpeg";

        // 对齐 Go Init 的 normalizeCallbackConcurrency：≤0 回落默认值 1。
        Addition.CallbackConcurrency = CallbackLimiterRegistry.Normalize(Addition.CallbackConcurrency);

        if (string.IsNullOrWhiteSpace(Addition.RefreshToken))
            throw new InvalidOperationException("refresh_token is required");

        // 阶段一：全局槽位限流器。失败时必须归还，否则该槽位的引用计数会永久泄漏。
        _limiter = AliyunLimiterRegistry.Acquire(AliyunLimiterRegistry.GlobalUserId);
        try
        {
            var text = await RequestApiAsync(AliyunLimiterKind.Other, "/adrive/v1.0/user/getDriveInfo", null, ct)
                .ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            DriveId = JsonValue.GetString(doc.RootElement, Addition.DriveType + "_drive_id") ?? string.Empty;
            UserId = JsonValue.GetString(doc.RootElement, "user_id") ?? string.Empty;
        }
        catch
        {
            _limiter.Dispose();
            _limiter = null;
            throw;
        }

        // 阶段二：换成真实用户槽位并注册回调准入（对齐 Go Init 尾部的
        // getLimiterForUser(userid) + registerCallbackLimiter(userid, CallbackConcurrency)）。
        _limiter.Dispose();
        _limiter = AliyunLimiterRegistry.Acquire(UserId);
        AttachCallbackLimiter(UserId);

        _fs = new AliyundriveOpenFileSystem(this);
        System = _fs;
    }

    /// <summary>
    /// 注册按阿里云用户共享的回调准入名额。重复调用会先注销旧注册，
    /// 保证每个驱动实例始终至多持有一个名额；驱动释放时自动注销。
    /// </summary>
    /// <param name="userId">云盘 API 返回的 user_id（共享配额的身份键）。</param>
    internal void AttachCallbackLimiter(string userId)
    {
        _callback?.Dispose();
        _callback = CallbackLimiterRegistry.Register(userId, Addition.CallbackConcurrency);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _callback?.Dispose();
        _callback = null;
        _limiter?.Dispose();
        _limiter = null;
        _fs = null;
        base.Dispose();
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------
    // 令牌刷新
    // ------------------------------------------------------------------

    /// <summary>
    /// 执行一次令牌刷新。两条路径对齐 Go <c>_refreshToken</c>：
    /// <list type="number">
    /// <item><c>use_online_api</c> 且配置了地址：GET 在线续期服务，按其返回的
    /// <c>access_token</c> / <c>refresh_token</c> 更新；响应体里的 <c>text</c> 是失败原因。</item>
    /// <item>否则走自建应用的本地 OAuth：POST <c>/oauth/access_token</c>，
    /// 并校验新旧 refresh_token 的 JWT <c>sub</c> 一致（防止换绑到别的账号）。</item>
    /// </list>
    /// <para>
    /// 两条路径都必须返回非空令牌 —— 空 token 写回会把存储彻底弄坏
    /// （<see cref="OAuthTokenRefresher"/> 也会把空 access_token 判为失败）。
    /// </para>
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新的令牌对。</returns>
    protected override async Task<OAuthTokens> RefreshTokenOnceAsync(CancellationToken ct)
    {
        if (Addition.UseOnlineApi && !string.IsNullOrEmpty(Addition.ApiUrlAddress))
        {
            // 对齐 Go：TV 端与普通端用不同的 driver_txt 走不同续期通道。
            var driverTxt = Addition.AlipanType == "alipanTV" ? "alicloud_tv" : "alicloud_qr";
            var url = Addition.ApiUrlAddress
                      + (Addition.ApiUrlAddress.Contains('?') ? "&" : "?")
                      + "refresh_ui=" + Uri.EscapeDataString(Addition.RefreshToken)
                      + "&server_use=true"
                      + "&driver_txt=" + driverTxt;

            await WaitLimiterAsync(AliyunLimiterKind.Other, ct).ConfigureAwait(false);
            using var response = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            string? refresh = null, access = null, error = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                refresh = JsonValue.GetString(doc.RootElement, "refresh_token");
                access = JsonValue.GetString(doc.RootElement, "access_token");
                error = JsonValue.GetString(doc.RootElement, "text");
            }
            catch (JsonException)
            {
                throw new InvalidOperationException($"failed to refresh token: invalid response {Truncate(body, 256)}");
            }

            if (string.IsNullOrEmpty(refresh) || string.IsNullOrEmpty(access))
            {
                throw new InvalidOperationException(!string.IsNullOrEmpty(error)
                    ? $"failed to refresh token: {error}"
                    : "empty token returned from official API, a wrong refresh token may have been used");
            }
            return new OAuthTokens(access, refresh);
        }

        if (string.IsNullOrEmpty(Addition.ClientId) || string.IsNullOrEmpty(Addition.ClientSecret))
            throw new InvalidOperationException("empty ClientID or ClientSecret");

        await WaitLimiterAsync(AliyunLimiterKind.Other, ct).ConfigureAwait(false);
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["client_id"] = Addition.ClientId,
            ["client_secret"] = Addition.ClientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = Addition.RefreshToken,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl + "/oauth/access_token")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        using var resp = await HttpClient.SendAsync(request, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        using var doc2 = JsonDocument.Parse(text);
        // 该接口失败时返回 {code, message}，成功时不含 code。
        var code = JsonValue.GetString(doc2.RootElement, "code");
        if (!string.IsNullOrEmpty(code))
        {
            var message = JsonValue.GetString(doc2.RootElement, "message");
            throw new InvalidOperationException($"failed to refresh token: {message}");
        }

        var newRefresh = JsonValue.GetString(doc2.RootElement, "refresh_token");
        var newAccess = JsonValue.GetString(doc2.RootElement, "access_token");
        if (string.IsNullOrEmpty(newRefresh))
            throw new InvalidOperationException($"failed to refresh token: refresh token is empty, resp: {Truncate(text, 256)}");

        // 只有两级都取到 sub 且相等才放行：JWT 结构异常或 sub 变化都视为失败。
        var currentSub = ReadJwtSubject(Addition.RefreshToken);
        var newSub = ReadJwtSubject(newRefresh);
        if (!string.Equals(currentSub, newSub, StringComparison.Ordinal))
            throw new InvalidOperationException("failed to refresh token: sub not match");

        return new OAuthTokens(newAccess ?? string.Empty, newRefresh);
    }

    /// <summary>
    /// 把刷新得到的新令牌写回 <see cref="Addition"/>。
    /// 对齐 Go <c>refreshToken()</c>：部分服务的刷新响应不再返回 refresh_token，此时保留旧值。
    /// </summary>
    /// <param name="tokens">新令牌。</param>
    protected override void ApplyTokens(OAuthTokens tokens)
    {
        Addition.AccessToken = tokens.AccessToken;
        if (!string.IsNullOrEmpty(tokens.RefreshToken)) Addition.RefreshToken = tokens.RefreshToken;
    }

    /// <summary>解析 JWT 的 <c>sub</c> 声明（不校验签名，只用于比对账号是否被换掉）。</summary>
    /// <param name="token">JWT 字符串。</param>
    /// <returns>sub 值；不是合法三段式 JWT 时返回 null。</returns>
    private static string? ReadJwtSubject(string token)
    {
        var segments = token.Split('.');
        if (segments.Length != 3) return null;
        try
        {
            var payload = Base64UrlDecode(segments[1]);
            using var doc = JsonDocument.Parse(payload);
            return JsonValue.GetString(doc.RootElement, "sub");
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>base64url 解码（补足 padding 后按标准 base64 解析）。</summary>
    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        // 括号不能省：switch 表达式会先与 `4` 结合，写成 `len % 4 switch` 会被解析成 `len % (4 switch ...)`。
        return (normalized.Length % 4) switch
        {
            2 => Convert.FromBase64String(normalized + "=="),
            3 => Convert.FromBase64String(normalized + "="),
            _ => Convert.FromBase64String(normalized),
        };
    }

    // ------------------------------------------------------------------
    // 请求封装
    // ------------------------------------------------------------------

    /// <summary>
    /// 发起一次云盘 API 调用（POST + Bearer 令牌），并把业务错误码翻译成异常。
    /// <para>
    /// 对齐 Go <c>requestReturnErrResp</c>：当错误码属于令牌失效类（<c>AccessTokenInvalid</c> /
    /// <c>AccessTokenExpired</c> / <c>I400JD</c>）或本地根本没有 access_token 时，
    /// **先刷新令牌再重试一次**；重试仍失败才上报错误，避免递归刷新。
    /// </para>
    /// </summary>
    /// <param name="kind">请求类别（决定走哪档限流配额）。</param>
    /// <param name="uri">相对 API 根地址的路径。</param>
    /// <param name="body">请求体对象；null 表示无请求体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="isRetry">内部标记：本次是否为刷新后的重试。</param>
    /// <returns>原始响应文本。</returns>
    private async Task<string> RequestApiAsync(
        AliyunLimiterKind kind, string uri, object? body, CancellationToken ct, bool isRetry = false)
    {
        await WaitLimiterAsync(kind, ct).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl + uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Addition.AccessToken);
        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        using var response = await HttpClient.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var (code, message) = ParseErrorCode(text);
        if (!string.IsNullOrEmpty(code))
        {
            if (!isRetry && (IsTokenErrorCode(code) || string.IsNullOrEmpty(Addition.AccessToken)))
            {
                await RefreshAndPersistTokensAsync(ct).ConfigureAwait(false);
                return await RequestApiAsync(kind, uri, body, ct, isRetry: true).ConfigureAwait(false);
            }
            throw new AliyunApiException(code!, message ?? string.Empty);
        }

        if (!response.IsSuccessStatusCode)
            throw new AliyunApiException($"HTTP {(int)response.StatusCode}", Truncate(text, 512));

        return text;
    }

    /// <summary>令牌失效类错误码（对齐 Go 的重试判定列表）。</summary>
    private static bool IsTokenErrorCode(string code)
        => code is "AccessTokenInvalid" or "AccessTokenExpired" or "I400JD";

    /// <summary>从响应文本里提取 <c>code</c> / <c>message</c>；非 JSON 对象或成功响应返回空。</summary>
    private static (string? Code, string? Message) ParseErrorCode(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null);
            return (JsonValue.GetString(doc.RootElement, "code"), JsonValue.GetString(doc.RootElement, "message"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>截断过长的响应文本，避免把整个页面塞进异常消息。</summary>
    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    /// <summary>按类别等待限流放行；驱动未初始化时给出明确错误（对齐 Go 的 <c>driver not init</c>）。</summary>
    private Task WaitLimiterAsync(AliyunLimiterKind kind, CancellationToken ct)
    {
        var limiter = _limiter ?? throw new InvalidOperationException("AliyundriveOpen driver is not initialized");
        return limiter.WaitAsync(kind, ct);
    }

    // ------------------------------------------------------------------
    // 云盘操作（供 Zio 视图与下载通道调用）
    // ------------------------------------------------------------------

    /// <summary>
    /// 列出指定目录 id 下的全部条目（自动翻页，每页 200 条）。
    /// 对齐 Go <c>getFiles</c>：首轮 marker 为空字符串，之后沿用服务端返回的 <c>next_marker</c>。
    /// </summary>
    /// <param name="fileId">目录 id。</param>
    /// <param name="ct">取消令牌。</param>
    internal async Task<List<AliyunFile>> GetFilesAsync(string fileId, CancellationToken ct = default)
    {
        var all = new List<AliyunFile>();
        var marker = string.Empty;

        while (true)
        {
            // 空值统一发空字符串（而非 null）：服务端对 null 的接受度不明确，Go 也是发 ""。
            var body = new Dictionary<string, object?>
            {
                ["drive_id"] = DriveId,
                ["limit"] = 200,
                ["marker"] = marker,
                ["order_by"] = Addition.OrderBy ?? string.Empty,
                ["order_direction"] = Addition.OrderDirection ?? string.Empty,
                ["parent_file_id"] = fileId,
            };

            var text = await RequestApiAsync(AliyunLimiterKind.List, "/adrive/v1.0/openFile/list", body, ct)
                .ConfigureAwait(false);
            var page = JsonSerializer.Deserialize<AliyunFiles>(text) ?? new AliyunFiles();
            all.AddRange(page.Items);

            if (string.IsNullOrEmpty(page.NextMarker)) break;
            marker = page.NextMarker;
        }

        return all;
    }

    /// <summary>新建目录，返回创建出的对象（对齐 Go <c>MakeDir</c>）。</summary>
    /// <param name="parentId">父目录 id。</param>
    /// <param name="name">目录名。</param>
    /// <param name="ct">取消令牌。</param>
    internal async Task<AliyunFile> MakeDirAsync(string parentId, string name, CancellationToken ct = default)
    {
        // create 接口不回时间字段，Go 用当前时间兜底；这里保持一致。
        var now = DateTimeOffset.UtcNow;
        var body = new Dictionary<string, object?>
        {
            ["drive_id"] = DriveId,
            ["parent_file_id"] = parentId,
            ["name"] = name,
            ["type"] = "folder",
            // refuse：目标位置已有同名目录时直接报错，而不是静默改名。
            ["check_name_mode"] = "refuse",
        };

        var text = await RequestApiAsync(AliyunLimiterKind.Other, "/adrive/v1.0/openFile/create", body, ct)
            .ConfigureAwait(false);
        var created = JsonSerializer.Deserialize<AliyunFile>(text) ?? new AliyunFile();
        created.CreatedAt ??= now;
        created.UpdatedAt ??= now;
        created.Type ??= "folder";
        return created;
    }

    /// <summary>
    /// 移动到目标父目录，并在需要时改名。对齐 Go <c>Move</c>：
    /// <c>openFile/move</c> 只换父目录（<c>check_name_mode=ignore</c> 容忍同名），
    /// 改名另走 <c>openFile/update</c>。
    /// </summary>
    /// <param name="src">源对象。</param>
    /// <param name="toParentId">目标父目录 id。</param>
    /// <param name="newName">目标名字；与源同名时跳过改名步骤。</param>
    /// <param name="ct">取消令牌。</param>
    internal async Task MoveAsync(AliyunFile src, string toParentId, string? newName, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["drive_id"] = DriveId,
            ["file_id"] = src.FileId,
            ["to_parent_file_id"] = toParentId,
            ["check_name_mode"] = "ignore",
        };

        var text = await RequestApiAsync(AliyunLimiterKind.Other, "/adrive/v1.0/openFile/move", body, ct)
            .ConfigureAwait(false);

        // 跨 drive 移动时 file_id 会变化，后续改名必须用新的 id。
        var moved = JsonSerializer.Deserialize<AliyunMoveOrCopyResp>(text);
        var currentId = moved?.FileId ?? src.FileId;

        if (!string.IsNullOrEmpty(newName) &&
            !string.Equals(newName, src.EffectiveName, StringComparison.Ordinal))
        {
            await RenameAsync(currentId!, newName, ct).ConfigureAwait(false);
        }
    }

    /// <summary>重命名对象（对齐 Go <c>Rename</c> 的 <c>openFile/update</c>）。</summary>
    /// <param name="fileId">目标文件 id。</param>
    /// <param name="newName">新名字。</param>
    /// <param name="ct">取消令牌。</param>
    internal async Task RenameAsync(string fileId, string newName, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["drive_id"] = DriveId,
            ["file_id"] = fileId,
            ["name"] = newName,
        };
        await RequestApiAsync(AliyunLimiterKind.Other, "/adrive/v1.0/openFile/update", body, ct)
            .ConfigureAwait(false);
    }

    /// <summary>服务端复制（对齐 Go <c>Copy</c>）。</summary>
    /// <param name="src">源对象。</param>
    /// <param name="toParentId">目标父目录 id。</param>
    /// <param name="newName">目标名；空表示沿用源名。目标已存在时会自动改名。</param>
    /// <param name="ct">取消令牌。</param>
    internal async Task CopyAsync(AliyunFile src, string toParentId, string? newName, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["drive_id"] = DriveId,
            ["file_id"] = src.FileId,
            ["to_parent_file_id"] = toParentId,
            // auto_rename=true：同名时由服务端改名，避免复制直接失败（Zio 的 CopyFile 语义也是覆盖）。
            ["auto_rename"] = true,
        };
        await RequestApiAsync(AliyunLimiterKind.Other, "/adrive/v1.0/openFile/copy", body, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 删除对象。按 <c>remove_way</c> 选择进回收站（默认）或彻底删除
    /// （对齐 Go <c>Remove</c>）。目录的子树由服务端一并处理。
    /// </summary>
    /// <param name="file">目标对象。</param>
    /// <param name="ct">取消令牌。</param>
    internal async Task RemoveAsync(AliyunFile file, CancellationToken ct = default)
    {
        var uri = Addition.RemoveWay == "delete"
            ? "/adrive/v1.0/openFile/delete"
            : "/adrive/v1.0/openFile/recyclebin/trash";

        var body = new Dictionary<string, object?>
        {
            ["drive_id"] = DriveId,
            ["file_id"] = file.FileId,
        };
        await RequestApiAsync(AliyunLimiterKind.Other, uri, body, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // 下载
    // ------------------------------------------------------------------

    /// <summary>按路径取直链（<see cref="IFileSystemLinkProvider"/> 的兼容入口）。</summary>
    /// <param name="path">路径（完整挂载路径或驱动相对路径均可）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<Uri?> GetLinkAsync(string path, CancellationToken ct = default)
    {
        var link = await GetFileLinkAsync(path, proxy: false, ct).ConfigureAwait(false);
        return link?.HasUrl == true ? new Uri(link.Url!) : null;
    }

    /// <summary>
    /// 取文件的下载通道。对齐 Go <c>Link</c>：
    /// 非代理模式返回直链（<c>/d/</c> 会 302 过去）；代理模式返回绑定回调准入的区间读端
    /// （<c>/p/</c> 由服务器按 Range 转发，全程占用一份按用户共享的并发配额）。
    /// </summary>
    /// <param name="path">路径（完整挂载路径或驱动相对路径均可）。</param>
    /// <param name="proxy">true 表示需要服务端代理流。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<FileLink?> GetFileLinkAsync(string path, bool proxy, CancellationToken ct = default)
    {
        var fs = _fs ?? throw new InvalidOperationException("AliyundriveOpen driver is not initialized");
        var relative = ToRelative(path);
        var file = await fs.ResolveAsync(new UPath(relative), ct).ConfigureAwait(false);
        if (file is null || file.IsFolder) return null;

        var url = await GetDownloadUrlAsync(file, ct).ConfigureAwait(false);

        // 链接有效期：Go 侧为 1 分钟（服务端 expire_sec 4 小时，但本地只当 1 分钟有效，
        // 以避免使用临近过期的地址）。
        var expiration = DateTimeOffset.UtcNow.AddMinutes(1);

        if (!proxy) return new FileLink { Url = url, Expiration = expiration };

        var registration = _callback
            ?? throw new CallbackCapacityException("Aliyun callback limiter is unavailable (driver not initialized)");
        var reader = new CallbackRangeReader(url, file.Size, registration);
        return new FileLink
        {
            RangeReader = (start, length, token) => reader.OpenAsync(start, length, token),
            Expiration = expiration,
        };
    }

    /// <summary>
    /// 请求下载地址。对齐 Go <c>Link</c> 的响应处理：<c>url</c> 为空时，
    /// 若是 livp 文件则从 <c>streamsUrl</c> 里按配置的格式（jpeg/mov）取地址。
    /// </summary>
    private async Task<string> GetDownloadUrlAsync(AliyunFile file, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["drive_id"] = DriveId,
            ["file_id"] = file.FileId,
            ["expire_sec"] = 14400,
        };

        var text = await RequestApiAsync(AliyunLimiterKind.Link, "/adrive/v1.0/openFile/getDownloadUrl", body, ct)
            .ConfigureAwait(false);

        using var doc = JsonDocument.Parse(text);
        var url = JsonValue.GetString(doc.RootElement, "url");
        if (!string.IsNullOrEmpty(url)) return url;

        var extension = Path.GetExtension(file.EffectiveName).TrimStart('.');
        if (!string.Equals(extension, "livp", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("get download url failed: " + Truncate(text, 512));

        // livp 是「实况照片」容器，服务端只给 streamsUrl 下的分轨地址。
        var streamUrl = JsonValue.GetString(doc.RootElement, "streamsUrl", Addition.LivpDownloadFormat);
        if (string.IsNullOrEmpty(streamUrl))
            throw new InvalidOperationException("get download url failed: " + Truncate(text, 512));
        return streamUrl;
    }

    /// <summary>
    /// 打开文件的读取流（供 Zio 层的读路径使用，例如复制、归档、扫描）。
    /// 走直链下载，不占用回调并发配额 —— 配额只保护「服务端代理」这条容易被限流的通道。
    /// </summary>
    /// <param name="path">路径（完整挂载路径或驱动相对路径均可）。</param>
    /// <param name="ct">取消令牌。</param>
    internal async Task<Stream> OpenReadStreamAsync(string path, CancellationToken ct = default)
    {
        var fs = _fs ?? throw new InvalidOperationException("AliyundriveOpen driver is not initialized");
        var relative = ToRelative(path);
        var file = await fs.ResolveAsync(new UPath(relative), ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"Path not found: {path}");
        if (file.IsFolder) throw new InvalidOperationException($"Cannot open a directory as a file: {path}");

        var url = await GetDownloadUrlAsync(file, ct).ConfigureAwait(false);
        var response = await HttpClient
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            response.Dispose();
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return new HttpContentStream(response, content);
    }

    // ------------------------------------------------------------------
    // 上传
    // ------------------------------------------------------------------

    /// <summary>上传文件到指定路径（路径的最后一段是文件名）。</summary>
    /// <param name="path">完整目标路径。</param>
    /// <param name="stream">内容流（不可 seek 时会先落到临时文件）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task PutAsync(string path, Stream stream, CancellationToken ct = default)
    {
        var relative = ToRelative(path);
        var directory = new UPath(relative).GetDirectory().FullName;
        var name = new UPath(relative).GetName();
        await UploadAsync(directory, name, stream, ct).ConfigureAwait(false);
    }

    /// <summary>表单上传：目录路径 + 文件名。</summary>
    /// <param name="path">目标目录。</param>
    /// <param name="stream">内容流。</param>
    /// <param name="fileName">文件名。</param>
    /// <param name="ct">取消令牌。</param>
    public Task PutFormAsync(string path, Stream stream, string fileName, CancellationToken ct = default)
    {
        var directory = ToRelative(path);
        return UploadAsync(directory, fileName, stream, ct);
    }

    /// <summary>
    /// 上传主流程，对齐 Go <c>upload</c>：create（可选 pre_hash 秒传）→ 分片 PUT → complete。
    /// </summary>
    /// <param name="directoryPath">目标目录的驱动相对路径。</param>
    /// <param name="name">文件名。</param>
    /// <param name="input">内容流。</param>
    /// <param name="ct">取消令牌。</param>
    private async Task UploadAsync(string directoryPath, string name, Stream input, CancellationToken ct)
    {
        var fs = _fs ?? throw new InvalidOperationException("AliyundriveOpen driver is not initialized");

        var dir = await fs.ResolveAsync(new UPath(directoryPath), ct).ConfigureAwait(false);
        var parentId = dir?.FileId ?? RootFolderId;

        // 分片与秒传都需要重新读取内容（算 pre_hash、全文 hash、逐片上传），
        // 因此不可 seek 的输入先落盘成临时文件。这是 .NET 侧的必要补偿：
        // Go 的 FileStreamer 本身就支持 RangeRead。
        var (body, ownsBody) = await EnsureSeekableAsync(input, ct).ConfigureAwait(false);
        try
        {
            var size = body.Length;
            var now = DateTimeOffset.UtcNow;
            var partSize = CalculatePartSize(size);

            var createData = new Dictionary<string, object?>
            {
                ["drive_id"] = DriveId,
                ["parent_file_id"] = parentId,
                ["name"] = name,
                ["type"] = "file",
                ["check_name_mode"] = "ignore",
                ["local_modified_at"] = FormatTimestamp(now),
                ["local_created_at"] = FormatTimestamp(now),
                ["part_info_list"] = MakePartInfos((int)Math.Ceiling((double)size / partSize)),
            };

            // 秒传准备：文件足够大且启用了 rapid_upload 时，先算前 1 KiB 的 SHA1 作为 pre_hash。
            var rapidUpload = Addition.RapidUpload && size > RapidUploadMinSize;
            if (rapidUpload)
            {
                createData["size"] = size;
                createData["pre_hash"] = await ComputePartialSha1Async(body, 1024, ct).ConfigureAwait(false);
            }

            AliyunCreateResp createResp;
            try
            {
                var text = await RequestApiAsync(AliyunLimiterKind.Other, "/adrive/v1.0/openFile/create", createData, ct)
                    .ConfigureAwait(false);
                createResp = JsonSerializer.Deserialize<AliyunCreateResp>(text) ?? new AliyunCreateResp();
            }
            catch (AliyunApiException ex) when (rapidUpload && ex.Code == "PreHashMatched")
            {
                // pre_hash 命中说明服务端已有相同前缀的候选，需要提交完整 SHA1 与 proof_code 完成秒传。
                var fullHash = await ComputeFullSha1Async(body, size, ct).ConfigureAwait(false);
                createData.Remove("pre_hash");
                createData["proof_version"] = "v1";
                createData["content_hash_name"] = "sha1";
                createData["content_hash"] = fullHash;
                createData["proof_code"] = await ComputeProofCodeAsync(body, size, ct).ConfigureAwait(false);

                var retryText = await RequestApiAsync(
                    AliyunLimiterKind.Other, "/adrive/v1.0/openFile/create", createData, ct).ConfigureAwait(false);
                createResp = JsonSerializer.Deserialize<AliyunCreateResp>(retryText) ?? new AliyunCreateResp();
            }

            if (!createResp.RapidUpload)
            {
                await UploadPartsAsync(body, partSize, createResp, ct).ConfigureAwait(false);
            }

            // complete：通知服务端分片上传结束，返回最终文件对象。
            var completeBody = new Dictionary<string, object?>
            {
                ["drive_id"] = DriveId,
                ["file_id"] = createResp.FileId,
                ["upload_id"] = createResp.UploadId,
            };
            await RequestApiAsync(AliyunLimiterKind.Other, "/adrive/v1.0/openFile/complete", completeBody, ct)
                .ConfigureAwait(false);

            // 新文件落位，父目录缓存作废。
            fs.InvalidateDirectory(new UPath(directoryPath));
        }
        finally
        {
            if (ownsBody) await body.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 逐片上传。对齐 Go：分片大小按文件大小阶梯选取，且上传地址可能在长时间传输后过期
    /// （Go 在超过 50 分钟时重新申请），这里对每片失败做 3 次退避重试。
    /// </summary>
    private async Task UploadPartsAsync(Stream body, long partSize, AliyunCreateResp createResp, CancellationToken ct)
    {
        var parts = createResp.PartInfoList;
        if (parts.Count == 0) return;

        for (var i = 0; i < parts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var offset = (long)i * partSize;
            if (offset >= body.Length) break;

            var length = (int)Math.Min(partSize, body.Length - offset);
            var chunk = new byte[length];
            body.Position = offset;
            await body.ReadExactlyAsync(chunk, ct).ConfigureAwait(false);

            await UploadPartWithRetryAsync(chunk, parts[i], ct).ConfigureAwait(false);
        }
    }

    /// <summary>单分片 PUT（含 3 次退避重试），对齐 Go 的 retry.Attempts(3) + BackOffDelay。</summary>
    private async Task UploadPartWithRetryAsync(byte[] chunk, AliyunPartInfo part, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                // 指数退避：1s → 2s，与 Go 的 Delay(time.Second) + BackOffDelay 同量级。
                await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), ct).ConfigureAwait(false);
            }

            try
            {
                var uploadUrl = part.UploadUrl ?? throw new InvalidOperationException("upload url is missing");
                if (Addition.InternalUpload)
                {
                    // 内网上传：把公网 OSS 域名换成阿里云北京内网域名（仅 ECS 同地域可用）。
                    uploadUrl = uploadUrl.Replace(
                        "https://cn-beijing-data.aliyundrive.net/",
                        "http://ccp-bj29-bj-1592982087.oss-cn-beijing-internal.aliyuncs.com/",
                        StringComparison.Ordinal);
                }

                using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
                {
                    Content = new ByteArrayContent(chunk),
                };
                using var response = await HttpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                // 409 表示该分片此前已上传成功（重试场景），视为成功（对齐 Go）。
                if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict) return;
                last = new InvalidOperationException($"upload part status: {(int)response.StatusCode}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw last ?? new InvalidOperationException("upload part failed");
    }

    /// <summary>
    /// 分片大小阶梯，对齐 Go <c>calPartSize</c>：默认 20 MB，
    /// 文件越大分片越大，以把分片数控制在服务端上限之内。
    /// </summary>
    private static long CalculatePartSize(long fileSize)
    {
        const long gb = 1024L * 1024 * 1024;
        const long tb = 1024L * gb;

        if (fileSize <= BasePartSize) return BasePartSize;
        if (fileSize > tb) return 5 * gb;
        if (fileSize > 768 * gb) return 109951163;
        if (fileSize > 512 * gb) return 82463373;
        if (fileSize > 384 * gb) return 54975582;
        if (fileSize > 256 * gb) return 41231687;
        if (fileSize > 128 * gb) return 27487791;
        return BasePartSize;
    }

    /// <summary>构造分片序号列表（<c>part_number</c> 从 1 开始），对齐 Go <c>makePartInfos</c>。</summary>
    private static List<Dictionary<string, object?>> MakePartInfos(int count)
    {
        var list = new List<Dictionary<string, object?>>(Math.Max(0, count));
        for (var i = 0; i < count; i++)
            list.Add(new Dictionary<string, object?> { ["part_number"] = i + 1 });
        return list;
    }

    /// <summary>ISO-8601 毫秒时间戳，格式对齐 Go 的 <c>2006-01-02T15:04:05.000Z</c>。</summary>
    private static string FormatTimestamp(DateTimeOffset time)
        => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>计算流前 <paramref name="count"/> 字节的 SHA1（十六进制小写），用于 pre_hash。</summary>
    private static async Task<string> ComputePartialSha1Async(Stream stream, int count, CancellationToken ct)
    {
        var length = (int)Math.Min(count, stream.Length);
        var buffer = new byte[length];
        stream.Position = 0;
        await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(SHA1.HashData(buffer));
    }

    /// <summary>计算整份内容的 SHA1（十六进制小写），用于秒传的 content_hash。</summary>
    private static async Task<string> ComputeFullSha1Async(Stream stream, long size, CancellationToken ct)
    {
        stream.Position = 0;
        var hash = await SHA1.HashDataAsync(new LimitedReadStream(stream, size), ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// 计算秒传凭据 <c>proof_code</c>，对齐 Go <c>calProofCode</c> + <c>getProofRange</c>：
    /// 用 MD5(access_token) 前 16 个十六进制字符对文件大小取模得到起点，
    /// 取该处连续 8 字节做 base64 —— 服务端用它验证客户端确实持有文件内容。
    /// </summary>
    private async Task<string> ComputeProofCodeAsync(Stream stream, long size, CancellationToken ct)
    {
        if (size == 0) return string.Empty;

        var md5Hex = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(Addition.AccessToken)))[..16];
        var index = (long)(ulong.Parse(md5Hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture) % (ulong)size);
        var end = Math.Min(index + 8, size);

        var buffer = new byte[end - index];
        // 需要读取 [index, end)。
        stream.Position = index;
        await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return Convert.ToBase64String(buffer);
    }

    /// <summary>
    /// 保证内容可重复读取：已是可 seek 流直接复用，否则先复制到临时文件
    /// （<c>DeleteOnClose</c> 保证异常路径也不留垃圾）。
    /// </summary>
    /// <returns>可 seek 的流，以及「是否由调用方负责释放」。</returns>
    private static async Task<(Stream Body, bool Owns)> EnsureSeekableAsync(Stream input, CancellationToken ct)
    {
        if (input.CanSeek) return (input, false);

        var temp = new FileStream(
            Path.GetTempFileName(),
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None,
            81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        await input.CopyToAsync(temp, ct).ConfigureAwait(false);
        temp.Position = 0;
        return (temp, true);
    }

    /// <summary>
    /// 把路径归一化为「相对挂载点」的形式：驱动内部一律使用相对路径
    /// （调用方可能给完整挂载路径，也可能已转换过）。
    /// </summary>
    private string ToRelative(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";

        if (!string.IsNullOrEmpty(MountPath) &&
            path.StartsWith(MountPath, StringComparison.OrdinalIgnoreCase))
        {
            var rest = path[MountPath.Length..];
            if (string.IsNullOrEmpty(rest)) return "/";
            return rest.StartsWith('/') ? rest : "/" + rest;
        }

        return path.StartsWith('/') ? path : "/" + path;
    }
}

/// <summary>云盘 API 返回业务错误码时抛出的异常，便于调用方按 <see cref="Code"/> 分支处理。</summary>
public sealed class AliyunApiException : Exception
{
    /// <summary>业务错误码（如 <c>PreHashMatched</c>、<c>AccessTokenInvalid</c>）。</summary>
    public string Code { get; }

    /// <summary>构造异常。</summary>
    /// <param name="code">业务错误码。</param>
    /// <param name="message">错误描述。</param>
    public AliyunApiException(string code, string message) : base($"{code}:{message}")
        => Code = code;
}

/// <summary>受限长度读取包装：把底层流的前 N 字节暴露为可哈希的流。</summary>
internal sealed class LimitedReadStream : Stream
{
    private readonly Stream _inner;
    private long _remaining;

    internal LimitedReadStream(Stream inner, long length)
    {
        _inner = inner;
        _remaining = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var allowed = (int)Math.Min(count, _remaining);
        if (allowed <= 0) return 0;
        var read = _inner.Read(buffer, offset, allowed);
        _remaining -= read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var allowed = (int)Math.Min(buffer.Length, _remaining);
        if (allowed <= 0) return 0;
        var read = await _inner.ReadAsync(buffer[..allowed], ct).ConfigureAwait(false);
        _remaining -= read;
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // 不释放内层流：它由调用方（上传主流程）统一管理。
        base.Dispose(disposing);
    }
}

/// <summary>JSON 取值小工具，避免到处写 <c>TryGetProperty</c> 样板。</summary>
internal static class JsonValue
{
    /// <summary>按属性名取字符串（非字符串或缺失时返回 null）。</summary>
    public static string? GetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };
    }

    /// <summary>按路径逐级取字符串，对齐 Go <c>utils.Json.Get(res, "a", "b")</c> 的用法。</summary>
    public static string? GetString(JsonElement element, string first, string second)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(first, out var nested)) return null;
        return GetString(nested, second);
    }
}

/// <summary>
/// 阿里云盘 Open 的附加配置，字段与 Go <c>aliyundrive_open.Addition</c> 一一对应。
/// 属性名即存储 Addition JSON 的键名。
/// </summary>
public sealed class AliyundriveOpenAddition
{
    /// <summary>要使用的网盘类型（default/resource/backup）。</summary>
    [JsonPropertyName("drive_type")]
    public string DriveType { get; set; } = "resource";

    /// <summary>挂载的根目录 id。</summary>
    [JsonPropertyName("root_folder_id")]
    public string RootFolderId { get; set; } = "root";

    /// <summary>OAuth 刷新令牌（必填）。</summary>
    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>默认排序字段。</summary>
    [JsonPropertyName("order_by")]
    public string? OrderBy { get; set; }

    /// <summary>默认排序方向。</summary>
    [JsonPropertyName("order_direction")]
    public string? OrderDirection { get; set; }

    /// <summary>是否使用在线 API 续期令牌。</summary>
    [JsonPropertyName("use_online_api")]
    public bool UseOnlineApi { get; set; } = true;

    /// <summary>客户端类型（default/alipanTV）。</summary>
    [JsonPropertyName("alipan_type")]
    public string AlipanType { get; set; } = "default";

    /// <summary>在线续期 API 地址。</summary>
    [JsonPropertyName("api_url_address")]
    public string ApiUrlAddress { get; set; } = "https://api.oplist.org/alicloud/renewapi";

    /// <summary>自建应用的 client_id，可留空。</summary>
    [JsonPropertyName("client_id")]
    public string? ClientId { get; set; }

    /// <summary>自建应用的 client_secret，可留空。</summary>
    [JsonPropertyName("client_secret")]
    public string? ClientSecret { get; set; }

    /// <summary>删除文件的方式（trash/delete）。</summary>
    [JsonPropertyName("remove_way")]
    public string RemoveWay { get; set; } = string.Empty;

    /// <summary>是否启用秒传。</summary>
    [JsonPropertyName("rapid_upload")]
    public bool RapidUpload { get; set; }

    /// <summary>是否启用内网上传（阿里云 ECS 北京地域）。</summary>
    [JsonPropertyName("internal_upload")]
    public bool InternalUpload { get; set; }

    /// <summary>livp 文件的下载格式（jpeg/mov）。</summary>
    [JsonPropertyName("livp_download_format")]
    public string LivpDownloadFormat { get; set; } = "jpeg";

    /// <summary>
    /// 同一阿里云用户共享的代理下载回调并发上限。对齐 Go
    /// <c>callback_concurrency</c>（<c>type:"number" default:"1"</c>）；
    /// ≤0 归一化为默认值 1（见 <see cref="CallbackLimiterRegistry.Normalize"/>）。
    /// </summary>
    [JsonPropertyName("callback_concurrency")]
    public int CallbackConcurrency { get; set; } = 1;

    /// <summary>
    /// 运行时访问令牌。<b>故意不带</b> <c>[JsonPropertyName]</c>：Go 的
    /// <c>AccessToken string</c> 同样没有 json tag，序列化后键名为 <c>AccessToken</c>，
    /// 这里保持一致以便与 Go 写出的 Addition JSON 互相读取。
    /// 它不出现在 <see cref="AliyundriveOpenDriver.ConfigSchema"/> 中，与 Go 行为一致
    /// （前端字段清单里没有 access_token）。
    /// </summary>
    public string AccessToken { get; set; } = string.Empty;
}
