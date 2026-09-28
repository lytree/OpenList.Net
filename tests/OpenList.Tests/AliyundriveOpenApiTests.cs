using System.Net;
using System.Text;
using OpenList.Core.Drivers;
using OpenList.Drivers.Cloud;
using OpenList.Drivers.Cloud.AliyundriveOpen;
using Zio;

namespace OpenList.Tests;

/// <summary>
/// 阿里云盘 Open 驱动（已移植的 API 部分）的测试。
/// <para>
/// 全部通过注入 <see cref="StubHandler"/> 拦截 HTTP，不访问真实云盘：
/// 覆盖初始化（getDriveInfo + 限流槽位切换）、路径解析与缓存、列目录、
/// 直链/代理通道选择、删除方式切换、以及令牌过期后的「刷新 + 重试一次」。
/// </para>
/// </summary>
public class AliyundriveOpenApiTests
{
    private const string DriveInfoJson = """{"user_id":"u-1","resource_drive_id":"drive-1","default_drive_id":"drive-d"}""";

    /// <summary>开启「在线续期 API」的配置（默认 api_url_address 指向 api.oplist.org/alicloud/renewapi）。</summary>
    private const string OnlineApiAddition = """{"refresh_token":"ref","use_online_api":true,"alipan_type":"default"}""";

    /// <summary>记录每次请求的 URI、请求体与 Authorization 头，供断言检查。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) => _respond = respond;

        public List<Call> Calls { get; } = [];

        public sealed record Call(string Uri, string Body, string? Authorization);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            lock (Calls)
            {
                Calls.Add(new Call(
                    request.RequestUri!.ToString(),
                    body,
                    request.Headers.Authorization?.Parameter));
            }
            return _respond(request, body);
        }
    }

    /// <summary>构造一个 JSON 响应。</summary>
    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>
    /// 按 URI 片段路由的最小 API 桩：未命中的请求返回 404，避免测试静默走错分支。
    /// </summary>
    private static StubHandler Handler(params (string Contains, Func<string, HttpResponseMessage> Respond)[] routes)
        => new((req, body) =>
        {
            var uri = req.RequestUri!.ToString();
            foreach (var (contains, respond) in routes)
            {
                if (uri.Contains(contains, StringComparison.Ordinal)) return respond(body);
            }
            return Json("""{"code":"NotFound","message":"no stub route"}""", HttpStatusCode.NotFound);
        });

    /// <summary>初始化驱动（默认走在线续期 API 关闭、自建应用凭据齐全的配置）。</summary>
    private static async Task<AliyundriveOpenDriver> InitAsync(
        StubHandler handler,
        string addition = """{"refresh_token":"ref","use_online_api":false,"client_id":"cid","client_secret":"sec"}""")
    {
        var driver = new AliyundriveOpenDriver();
        driver.UseHttpClient(new HttpClient(handler));
        await driver.InitAsync(new DriverConfig { MountPath = "/ali", Driver = "AliyundriveOpen", Addition = addition });
        return driver;
    }

    // ------------------------------------------------------------------
    // 初始化
    // ------------------------------------------------------------------

    [Test]
    public async Task InitAsync_ReadsDriveIdAndUserId_AndMountsFileSystem()
    {
        using var handler = Handler(("user/getDriveInfo", _ => Json(DriveInfoJson)));
        using var driver = await InitAsync(handler);

        await Assert.That(driver.DriveId).IsEqualTo("drive-1");
        await Assert.That(driver.UserId).IsEqualTo("u-1");
        // 移植后不再保持 System 为 null —— 文件系统已可用。
        await Assert.That(driver.System).IsNotNull();

        // 首个请求带 Bearer 令牌（此时为空字符串，服务端会要求刷新）。
        var call = handler.Calls.Single();
        await Assert.That(call.Uri).Contains("/adrive/v1.0/user/getDriveInfo");
        await Assert.That(call.Authorization).IsEqualTo("");
    }

    [Test]
    public async Task InitAsync_DriveTypeSelectsMatchingDriveId()
    {
        // drive_type=backup 时应取 backup_drive_id，而不是 resource_drive_id。
        using var handler = Handler(("getDriveInfo",
            _ => Json("""{"user_id":"u-1","resource_drive_id":"r","backup_drive_id":"b"}""")));
        using var driver = await InitAsync(handler,
            """{"refresh_token":"ref","drive_type":"backup"}""");

        await Assert.That(driver.DriveId).IsEqualTo("b");
    }

    [Test]
    public async Task InitAsync_AppliesDefaults_ForEmptyFields()
    {
        using var handler = Handler(("user/getDriveInfo", _ => Json(DriveInfoJson)));
        using var driver = await InitAsync(handler,
            """{"refresh_token":"ref","drive_type":"","livp_download_format":""}""");

        // 对齐 Go Init 的空值补默认行为。
        await Assert.That(driver.Addition.DriveType).IsEqualTo("default");
        await Assert.That(driver.Addition.LivpDownloadFormat).IsEqualTo("jpeg");
        // drive_type 补成 "default" 后，drive_id 应从 default_drive_id 取。
        await Assert.That(driver.DriveId).IsEqualTo("drive-d");
    }

    [Test]
    public async Task InitAsync_NormalizesCallbackConcurrency()
    {
        using var handler = Handler(("user/getDriveInfo", _ => Json(DriveInfoJson)));
        using var driver = await InitAsync(handler,
            """{"refresh_token":"ref","callback_concurrency":0}""");

        // ≤0 必须归一化为默认值 1，否则回调准入会退化成「零并发」而永久阻塞下载。
        await Assert.That(driver.Addition.CallbackConcurrency).IsEqualTo(1);
    }

    [Test]
    public async Task InitAsync_SwitchesLimiterToPerUserSlot_AndReleasesOnDispose()
    {
        // 用独有的 user_id：测试是并行执行的，若共用 user_id，其他用例持有的租约
        // 会让本用例的「释放后回收」断言偶发失败。
        using var handler = Handler(("user/getDriveInfo",
            _ => Json("""{"user_id":"limiter-slot-user","resource_drive_id":"d"}""")));
        var driver = await InitAsync(handler);

        // 拿到 user_id 后，限流器应挂在真实用户槽位上（同账号多挂载点共享配额）。
        await Assert.That(AliyunLimiterRegistry.HasUser("limiter-slot-user")).IsTrue();
        await Assert.That(AliyunLimiterRegistry.HasUser(AliyunLimiterRegistry.GlobalUserId)).IsTrue();

        driver.Dispose();
        // 释放后用户槽位被回收；全局槽位常驻（对齐 Go 不删 globalLimiterUserID 的行为）。
        await Assert.That(AliyunLimiterRegistry.HasUser("limiter-slot-user")).IsFalse();
        await Assert.That(AliyunLimiterRegistry.HasUser(AliyunLimiterRegistry.GlobalUserId)).IsTrue();
    }

    [Test]
    public async Task InitAsync_RejectsMissingRefreshToken_WithoutAnyRequest()
    {
        using var handler = Handler();
        var driver = new AliyundriveOpenDriver();
        driver.UseHttpClient(new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await driver.InitAsync(new DriverConfig { Addition = "{}" }));

        // 配置不合法时不应发出任何请求，也绝不能泄漏全局限流槽位的引用计数。
        await Assert.That(handler.Calls).Count().IsEqualTo(0);
        await Assert.That(driver.System).IsNull();
    }

    // ------------------------------------------------------------------
    // 路径解析与列目录
    // ------------------------------------------------------------------

    [Test]
    public async Task List_RequestsConfiguredRootFolderId_AndExposesEntries()
    {
        const string listJson = """
        {"items":[
          {"file_id":"f1","name":"docs","type":"folder","size":0},
          {"file_id":"f2","name":"a.txt","type":"file","size":123}
        ],"next_marker":""}
        """;
        using var handler = Handler(
            ("user/getDriveInfo", _ => Json(DriveInfoJson)),
            ("openFile/list", _ => Json(listJson)));
        using var driver = await InitAsync(handler);

        var names = driver.System!
            .EnumeratePaths(UPath.Root, "*", SearchOption.TopDirectoryOnly, SearchTarget.Both)
            .Select(p => p.GetName())
            .OrderBy(n => n)
            .ToList();

        await Assert.That(names).IsEquivalentTo(new[] { "a.txt", "docs" });

        var listCall = handler.Calls.First(c => c.Uri.Contains("openFile/list"));
        await Assert.That(listCall.Body).Contains("\"parent_file_id\":\"root\"");
        await Assert.That(listCall.Body).Contains("\"limit\":200");
    }

    [Test]
    public async Task List_FollowsNextMarker_UntilExhausted()
    {
        var page = 0;
        using var handler = Handler(
            ("user/getDriveInfo", _ => Json(DriveInfoJson)),
            ("openFile/list", _ => Json(page++ == 0
                ? """{"items":[{"file_id":"f1","name":"a","type":"file","size":1}],"next_marker":"m2"}"""
                : """{"items":[{"file_id":"f2","name":"b","type":"file","size":2}],"next_marker":""}""")));
        using var driver = await InitAsync(handler);

        var names = driver.System!
            .EnumeratePaths(UPath.Root, "*", SearchOption.TopDirectoryOnly, SearchTarget.Both)
            .Select(p => p.GetName())
            .OrderBy(n => n)
            .ToList();

        await Assert.That(names).IsEquivalentTo(new[] { "a", "b" });
        // 第二页必须带上服务端下发的 marker，否则会原地翻页死循环。
        var calls = handler.Calls.Where(c => c.Uri.Contains("openFile/list")).ToList();
        await Assert.That(calls).Count().IsEqualTo(2);
        await Assert.That(calls[1].Body).Contains("\"marker\":\"m2\"");
    }

    [Test]
    public async Task Resolve_ReusesCachedPath_AcrossRepeatedAccess()
    {
        const string listJson = """{"items":[{"file_id":"f1","name":"a.txt","type":"file","size":10}],"next_marker":""}""";
        using var handler = Handler(
            ("user/getDriveInfo", _ => Json(DriveInfoJson)),
            ("openFile/list", _ => Json(listJson)));
        using var driver = await InitAsync(handler);

        var path = UPath.Root / "a.txt";
        _ = driver.System!.GetFileLength(path);
        _ = driver.System!.GetFileLength(path);
        _ = driver.System!.FileExists(path);

        // 三次访问只应产生一次 list：解析结果进了路径缓存。
        await Assert.That(handler.Calls.Count(c => c.Uri.Contains("openFile/list"))).IsEqualTo(1);
    }

    // ------------------------------------------------------------------
    // 下载通道
    // ------------------------------------------------------------------

    [Test]
    public async Task GetFileLinkAsync_Redirect_ReturnsUrlWithoutRangeReader()
    {
        const string listJson = """{"items":[{"file_id":"f1","name":"a.txt","type":"file","size":10}],"next_marker":""}""";
        using var handler = Handler(
            ("user/getDriveInfo", _ => Json(DriveInfoJson)),
            ("openFile/list", _ => Json(listJson)),
            ("getDownloadUrl", _ => Json("""{"url":"https://cdn.example/a.txt"}""")));
        using var driver = await InitAsync(handler);

        var link = await driver.GetFileLinkAsync("/a.txt", proxy: false);

        await Assert.That(link).IsNotNull();
        await Assert.That(link!.Url).IsEqualTo("https://cdn.example/a.txt");
        // 直链模式不该占用回调并发通道。
        await Assert.That(link.RangeReader).IsNull();
    }

    [Test]
    public async Task GetFileLinkAsync_Proxy_ReturnsRangeReaderWithoutUrl()
    {
        const string listJson = """{"items":[{"file_id":"f1","name":"a.txt","type":"file","size":10}],"next_marker":""}""";
        using var handler = Handler(
            ("user/getDriveInfo", _ => Json(DriveInfoJson)),
            ("openFile/list", _ => Json(listJson)),
            ("getDownloadUrl", _ => Json("""{"url":"https://cdn.example/a.txt"}""")));
        using var driver = await InitAsync(handler);

        var link = await driver.GetFileLinkAsync("/a.txt", proxy: true);

        await Assert.That(link).IsNotNull();
        await Assert.That(link!.Url).IsNull();
        await Assert.That(link.RangeReader).IsNotNull();
    }

    [Test]
    public async Task GetFileLinkAsync_LivpFallsBackToStreamsUrl()
    {
        const string listJson = """{"items":[{"file_id":"f1","name":"live.livp","type":"file","size":10}],"next_marker":""}""";
        using var handler = Handler(
            ("user/getDriveInfo", _ => Json(DriveInfoJson)),
            ("openFile/list", _ => Json(listJson)),
            ("getDownloadUrl", _ => Json("""{"streamsUrl":{"jpeg":"https://cdn.example/live.jpg"}}""")));
        using var driver = await InitAsync(handler);

        var link = await driver.GetFileLinkAsync("/live.livp", proxy: false);

        // livp 容器没有整体直链，必须按配置的格式取分轨地址。
        await Assert.That(link!.Url).IsEqualTo("https://cdn.example/live.jpg");
    }

    [Test]
    public async Task GetFileLinkAsync_Directory_ReturnsNull()
    {
        const string listJson = """{"items":[{"file_id":"d1","name":"docs","type":"folder","size":0}],"next_marker":""}""";
        using var handler = Handler(
            ("user/getDriveInfo", _ => Json(DriveInfoJson)),
            ("openFile/list", _ => Json(listJson)));
        using var driver = await InitAsync(handler);

        await Assert.That(await driver.GetFileLinkAsync("/docs", proxy: false)).IsNull();
    }

    // ------------------------------------------------------------------
    // 删除方式
    // ------------------------------------------------------------------

    [Test]
    public async Task Remove_UsesTrashByDefault_AndDeleteWhenConfigured()
    {
        const string listJson = """{"items":[{"file_id":"f1","name":"a.txt","type":"file","size":10}],"next_marker":""}""";
        using var trashHandler = Handler(
            ("user/getDriveInfo", _ => Json(DriveInfoJson)),
            ("openFile/list", _ => Json(listJson)),
            ("recyclebin/trash", _ => Json("{}")));
        using var trashDriver = await InitAsync(trashHandler);
        trashDriver.System!.DeleteFile(UPath.Root / "a.txt");
        await Assert.That(trashHandler.Calls.Any(c => c.Uri.Contains("recyclebin/trash"))).IsTrue();

        using var deleteHandler = Handler(
            ("user/getDriveInfo", _ => Json(DriveInfoJson)),
            ("openFile/list", _ => Json(listJson)),
            ("openFile/delete", _ => Json("{}")));
        using var deleteDriver = await InitAsync(deleteHandler,
            """{"refresh_token":"ref","use_online_api":false,"client_id":"cid","client_secret":"sec","remove_way":"delete"}""");
        deleteDriver.System!.DeleteFile(UPath.Root / "a.txt");
        await Assert.That(deleteHandler.Calls.Any(c => c.Uri.Contains("openFile/delete"))).IsTrue();
    }

    // ------------------------------------------------------------------
    // 令牌刷新
    // ------------------------------------------------------------------

    [Test]
    public async Task TokenExpired_RefreshesToken_ThenRetriesRequestOnce()
    {
        var listAttempts = 0;
        using var handler = Handler(
            ("user/getDriveInfo", _ => Json(DriveInfoJson)),
            // 首次 list 报「令牌过期」，刷新后重试才返回数据。
            ("openFile/list", _ =>
            {
                listAttempts++;
                return listAttempts == 1
                    ? Json("""{"code":"AccessTokenExpired","message":"expired"}""")
                    : Json("""{"items":[{"file_id":"f1","name":"a.txt","type":"file","size":1}],"next_marker":""}""");
            }),
            ("renewapi", _ => Json("""{"access_token":"acc-2","refresh_token":"ref-2"}""")));
        using var driver = await InitAsync(handler, OnlineApiAddition);

        var names = driver.System!
            .EnumeratePaths(UPath.Root, "*", SearchOption.TopDirectoryOnly, SearchTarget.Both)
            .Select(p => p.GetName())
            .ToList();

        await Assert.That(names).IsEquivalentTo(new[] { "a.txt" });
        await Assert.That(listAttempts).IsEqualTo(2);
        // 刷新后的新令牌必须落到后续请求上。
        await Assert.That(driver.Addition.AccessToken).IsEqualTo("acc-2");
        await Assert.That(driver.Addition.RefreshToken).IsEqualTo("ref-2");
        await Assert.That(handler.Calls.Last(c => c.Uri.Contains("openFile/list")).Authorization).IsEqualTo("acc-2");
    }

    [Test]
    public async Task RefreshToken_FailsWhenOnlineApiReturnsEmptyTokens()
    {
        using var handler = Handler(
            ("user/getDriveInfo", _ => Json(DriveInfoJson)),
            ("openFile/list", _ => Json("""{"code":"AccessTokenExpired","message":"expired"}""")),
            // 续期服务返回空令牌：必须直接失败，绝不能把空 token 写回存储。
            ("renewapi", _ => Json("""{"text":"refresh token is invalid"}""")));
        using var driver = await InitAsync(handler, OnlineApiAddition);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            _ = driver.System!
                .EnumeratePaths(UPath.Root, "*", SearchOption.TopDirectoryOnly, SearchTarget.Both)
                .ToList();
            return Task.CompletedTask;
        });

        await Assert.That(ex.Message).Contains("refresh token is invalid");
        await Assert.That(driver.Addition.AccessToken).IsEqualTo("");
    }

    [Test]
    public async Task RefreshToken_LocalOAuth_RejectsSubjectMismatch()
    {
        // 两段合法但 sub 不同的 JWT：payload 为 {"sub":"..."} 的 base64url 编码。
        const string oldToken = "h.eyJzdWIiOiJ1LTEifQ.s";
        const string newToken = "h.eyJzdWIiOiJ1LTk5In0.s";

        using var handler = Handler(
            ("user/getDriveInfo", _ => Json(DriveInfoJson)),
            ("openFile/list", _ => Json("""{"code":"AccessTokenExpired","message":"expired"}""")),
            ("oauth/access_token", _ => Json($$"""{"access_token":"acc-2","refresh_token":"{{newToken}}"}""")));
        using var driver = await InitAsync(handler,
            $$"""{"refresh_token":"{{oldToken}}","use_online_api":false,"client_id":"cid","client_secret":"sec"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            _ = driver.System!
                .EnumeratePaths(UPath.Root, "*", SearchOption.TopDirectoryOnly, SearchTarget.Both)
                .ToList();
            return Task.CompletedTask;
        });

        // sub 变化意味着 refresh_token 被换到别的账号上，必须拒绝。
        await Assert.That(ex.Message).Contains("sub not match");
    }

    [Test]
    public async Task RefreshToken_LocalOAuth_RequiresClientCredentials()
    {
        using var handler = Handler(
            ("user/getDriveInfo", _ => Json(DriveInfoJson)),
            ("openFile/list", _ => Json("""{"code":"AccessTokenExpired","message":"expired"}""")));
        using var driver = await InitAsync(handler, """{"refresh_token":"ref","use_online_api":false}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            _ = driver.System!
                .EnumeratePaths(UPath.Root, "*", SearchOption.TopDirectoryOnly, SearchTarget.Both)
                .ToList();
            return Task.CompletedTask;
        });

        await Assert.That(ex.Message).Contains("ClientID or ClientSecret");
    }

    // ------------------------------------------------------------------
    // 限流注册表
    // ------------------------------------------------------------------

    [Test]
    public async Task LimiterRegistry_SharesInstancePerUser_AndReclaimsWhenUnused()
    {
        var first = AliyunLimiterRegistry.Acquire("shared-user");
        var second = AliyunLimiterRegistry.Acquire("shared-user");

        // 两次取用必须是同一实例，否则「按用户共享配额」无从谈起。
        await Assert.That(ReferenceEquals(first.UserId, second.UserId)).IsTrue();
        await Assert.That(AliyunLimiterRegistry.HasUser("shared-user")).IsTrue();

        first.Dispose();
        // 仍有一个租约在手上，条目不能提前回收。
        await Assert.That(AliyunLimiterRegistry.HasUser("shared-user")).IsTrue();

        second.Dispose();
        await Assert.That(AliyunLimiterRegistry.HasUser("shared-user")).IsFalse();
    }

    [Test]
    public async Task LimiterLease_AfterDispose_UsesThrowsInsteadOfSilentlyPassing()
    {
        var lease = AliyunLimiterRegistry.Acquire("lease-user");
        lease.Dispose();

        // 释放后再用必须报错：静默放行会让限流形同虚设。
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await lease.WaitAsync(AliyunLimiterKind.Other));
    }
}
