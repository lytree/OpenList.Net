using System.Text.Json.Serialization;
using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;

namespace OpenList.Drivers.Cloud.AliyundriveOpen;

/// <summary>
/// 阿里云盘 Open（openapi.alipan.com）驱动。
/// <para>
/// <b>⚠️ 当前为骨架实现，未经真实服务验证，不可用于生产。</b>
/// 配置字段与驱动能力已完整对齐 Go 参考 <c>drivers/aliyundrive_open/meta.go</c>，
/// 因此管理端 <c>/api/admin/driver/list</c> 能正确列出该驱动并生成配置表单；
/// 但所有访问云盘 API 的操作（列举、下载链接、上传、删除、token 刷新）尚未移植，
/// 见 <c>NotImplemented</c> 区域。不存在于云盘真实数据的挂载点不会显示任何内容
/// （<see cref="ZioDriverBase.System"/> 故意保持为 null —— 挂载一个空文件系统会让用户
/// 误以为云盘是空的，比直接失败更糟）。
/// </para>
/// </summary>
public sealed class AliyundriveOpenDriver : CloudDriverBase, IFileSystemLinkProvider, IFileSystemUploader
{
    /// <summary>
    /// 回调准入注册句柄。Go 在 Init 拿到 user_id 后立即
    /// <c>registerCallbackLimiter(userid, d.CallbackConcurrency)</c> 并在 Drop 时注销；
    /// 本骨架尚未调用云盘 API、拿不到 user_id，因此在 API 移植后于取得 user_id 的位置
    /// 调用 <see cref="AttachCallbackLimiter"/>。释放驱动时自动注销（见 Dispose 覆写）。
    /// </summary>
    private CallbackLimiterRegistration? _callback;

    /// <summary>本驱动展示名，与 Go <c>driver.Config.Name</c> 一致（前端翻译键）。</summary>
    public override string DriverName => "AliyundriveOpen";

    /// <summary>默认根目录 id，对齐 Go <c>Config.DefaultRoot = "root"</c>。</summary>
    public override string DefaultRoot => "root";

    /// <summary>
    /// 配置页警示条。前端会把 <c>'|'</c> 前的一段按 info/success/warning/danger 上色显示，
    /// 用它明确告知使用者该驱动尚未实现，避免误配到生产环境。
    /// </summary>
    public override string Alert =>
        "warning|骨架实现：API 尚未移植，不返回任何文件。仅用于配置表单与后续移植，请勿用于生产。";

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

    /// <summary>
    /// 解析并校验配置。
    /// <para>
    /// 对齐 Go <c>Init</c> 的字段补默认值行为（<c>drive_type</c> / <c>livp_download_format</c>），
    /// 并额外校验 <c>refresh_token</c> —— Go 靠随后调用 API 才暴露该字段缺失，
    /// 骨架阶段没有 API 调用，因此在这里给出即时、明确的报错。
    /// </para>
    /// <para>
    /// 注意：成功返回后 <see cref="ZioDriverBase.System"/> 仍为 null，
    /// 挂载服务据此判定「无需挂载」，存储配置保存流程因此可以正常完成。
    /// </para>
    /// </summary>
    /// <param name="config">包含 Addition JSON 的驱动配置。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>表示初始化完成的任务。</returns>
    /// <exception cref="InvalidOperationException">refresh_token 缺失时抛出。</exception>
    public override Task InitAsync(DriverConfig config, CancellationToken ct = default)
    {
        Addition = config.GetAddition<AliyundriveOpenAddition>();

        // 与 Go 一致：空值补默认，避免下游拿到空字符串。
        if (string.IsNullOrEmpty(Addition.DriveType)) Addition.DriveType = "default";
        if (string.IsNullOrEmpty(Addition.LivpDownloadFormat)) Addition.LivpDownloadFormat = "jpeg";

        // 对齐 Go Init 的 normalizeCallbackConcurrency：≤0 回落默认值 1。
        Addition.CallbackConcurrency = CallbackLimiterRegistry.Normalize(Addition.CallbackConcurrency);

        if (string.IsNullOrWhiteSpace(Addition.RefreshToken))
            throw new InvalidOperationException("refresh_token is required");

        // Go 侧该驱动使用固定速率的三档限流器（list 3.9/s、link 0.9/s、other 14.9/s，
        // 见 drivers/aliyundrive_open/limiter.go），移植时在此接入。
        // 骨架阶段不配置限流（0 = 不限流），因为没有任何请求发出。
        ConfigureRateLimit(0);

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // 待移植的云盘 API 操作
    //
    // 下列成员构成移植 drivers/aliyundrive_open 的接口面。骨架阶段一律抛
    // NotImplementedException，作为后续分批实现的清单与占位。
    // 建议移植顺序：token 刷新 → 列目录 → 下载链接 → 上传 → 删除。
    //
    // 这些成员不会被当前代码路径触发：InitAsync 不设置 System，挂载服务因而不会
    // 注册该挂载点，FindDriverForPath 对其返回 null，服务层到不了这里。
    // ------------------------------------------------------------------

    /// <summary>云盘 API 根地址，对齐 Go <c>API_URL</c>。</summary>
    internal const string ApiUrl = "https://openapi.alipan.com";

    /// <summary>构造统一的「尚未移植」异常，便于日志与前端定位。</summary>
    private NotImplementedException NotImplemented(string operation)
        => new($"AliyundriveOpen skeleton: '{operation}' is not ported yet " +
               $"(Go reference: drivers/aliyundrive_open, API base {ApiUrl}).");

    /// <summary>
    /// 注册按阿里云用户共享的回调准入名额（对齐 Go Init 尾部的
    /// <c>d.callback = registerCallbackLimiter(userid, d.CallbackConcurrency)</c>）。
    /// 重复调用会先注销旧注册，保证每个驱动实例始终至多持有一个名额；
    /// 驱动释放时自动注销。API 移植后在拿到 user_id 处调用。
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
        base.Dispose();
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        _callback?.Dispose();
        _callback = null;
        return base.DisposeAsync();
    }

    /// <summary>
    /// 执行一次令牌刷新（POST <c>/adrive/v1.0/user/getToken</c>，或走
    /// <see cref="AliyundriveOpenAddition.ApiUrlAddress"/> 的在线续期 API）。
    /// 骨架阶段未移植，<see cref="CloudDriverBase.RefreshAndPersistTokensAsync"/> 会因此失败。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>永不返回。</returns>
    /// <exception cref="NotImplementedException">始终抛出。</exception>
    protected override Task<OAuthTokens> RefreshTokenOnceAsync(CancellationToken ct)
        => throw NotImplemented("refreshToken");

    /// <summary>
    /// 把刷新得到的新令牌写回 <see cref="Addition"/>。
    /// 这是纯内存状态更新，已按 Go 的 <c>refreshToken()</c> 逻辑实现，
    /// 但因为排在 <see cref="RefreshTokenOnceAsync"/> 之后，骨架阶段不会被触发。
    /// </summary>
    /// <param name="tokens">新令牌。</param>
    protected override void ApplyTokens(OAuthTokens tokens)
    {
        Addition.AccessToken = tokens.AccessToken;
        // 部分服务刷新响应里不再返回新的 refresh_token，此时必须保留旧值，
        // 否则会把续期能力彻底丢掉。
        if (!string.IsNullOrEmpty(tokens.RefreshToken)) Addition.RefreshToken = tokens.RefreshToken;
    }

    // --- IFileSystemLinkProvider（待移植）---

    /// <summary>生成文件直链（POST <c>/adrive/v1.0/openFile/getDownloadUrl</c>）。骨架阶段未移植。</summary>
    /// <param name="path">文件路径。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>永不返回。</returns>
    /// <exception cref="NotImplementedException">始终抛出。</exception>
    public Task<Uri?> GetLinkAsync(string path, CancellationToken ct = default)
        => throw NotImplemented("link");

    // --- IFileSystemUploader（待移植）---

    /// <summary>流式上传文件（含分片与秒传）。骨架阶段未移植。</summary>
    /// <param name="path">目标路径。</param>
    /// <param name="stream">内容流。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>永不返回。</returns>
    /// <exception cref="NotImplementedException">始终抛出。</exception>
    public Task PutAsync(string path, Stream stream, CancellationToken ct = default)
        => throw NotImplemented("put");

    /// <summary>表单上传文件。骨架阶段未移植。</summary>
    /// <param name="path">目标路径。</param>
    /// <param name="stream">内容流。</param>
    /// <param name="fileName">文件名。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>永不返回。</returns>
    /// <exception cref="NotImplementedException">始终抛出。</exception>
    public Task PutFormAsync(string path, Stream stream, string fileName, CancellationToken ct = default)
        => throw NotImplemented("putForm");
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
