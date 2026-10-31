using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TUnit.Core;

namespace OpenList.Tests;

/// <summary>
/// Integration tests for /api/fs endpoints, focusing on guest (anonymous) access
/// to the read endpoints (list/get/dirs/search) — Go's fsAndShare group behaviour.
/// Uses TestWebAppFactory with a Local storage mounted at /test.
/// </summary>
[NotInParallel("WebIntegration")]
public class FsEndpointsTests
{
    private static async Task<(TestWebAppFactory factory, HttpClient client)> SetupAsync()
    {
        var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        await factory.SeedStorageAsync();
        return (factory, client);
    }

    /// <summary>
    /// 打开「首页允许游客浏览」开关（<c>home_guest_access</c>）。
    /// <para>
    /// 默认是 **false** —— 首页（文件浏览）需要登录，匿名 <c>/api/fs/*</c> 直接 401。
    /// 下面几个「游客能读」的用例都建立在这条开关打开的前提下（公开只读分享站形态）。
    /// </para>
    /// </summary>
    private static Task AllowGuestBrowsingAsync(TestWebAppFactory factory)
        => factory.SetSettingAsync("home_guest_access", "true");

    [Test]
    public async Task List_Allows_Anonymous_Guest_Access()
    {
        // Go allows guest access to /api/fs/list via Auth(true) middleware.
        // .NET mirrors this with AllowAnonymous on the read endpoints.
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await AllowGuestBrowsingAsync(factory);

            await factory.WriteFileAsync("a.txt", "a"u8.ToArray());

            // No Authorization header — anonymous guest request.
            var resp = await client.PostAsync("/api/fs/list",
                JsonContent.Create(new { path = "/test" }));
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);
            await Assert.That(json.RootElement.GetProperty("data").GetProperty("total").GetInt32()).IsGreaterThan(0);
        }
    }

    [Test]
    public async Task Get_Allows_Anonymous_Guest_Access()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await AllowGuestBrowsingAsync(factory);

            await factory.WriteFileAsync("a.txt", "a"u8.ToArray());

            var resp = await client.PostAsync("/api/fs/get",
                JsonContent.Create(new { path = "/test/a.txt" }));
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);
        }
    }

    [Test]
    public async Task Dirs_Allows_Anonymous_Guest_Access()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await AllowGuestBrowsingAsync(factory);

            await factory.WriteFileAsync("a.txt", "a"u8.ToArray());

            var resp = await client.PostAsync("/api/fs/dirs",
                JsonContent.Create(new { path = "/test" }));
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);
        }
    }

    [Test]
    public async Task List_Denies_Anonymous_When_Home_Guest_Access_Is_Off()
    {
        // 默认策略：首页（文件浏览）需要登录。关掉 home_guest_access 时匿名 /fs/list 直接 401，
        // 而不是「200 + 空列表」—— 前端据此回登录页，第三方客户端也不会误以为拿到了目录。
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.txt", "a"u8.ToArray());

            var resp = await client.PostAsync("/api/fs/list",
                JsonContent.Create(new { path = "/test" }));
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(401);
        }
    }

    [Test]
    public async Task List_Still_Allows_Share_Code_When_Home_Guest_Access_Is_Off()
    {
        // 公开分享是既有契约，与「首页对游客开不开」无关：带分享码的匿名请求照旧放行，
        // 真伪交给 ShareAccess 判定（错误的分享码会拿到它自己的错误码，而不是网关层的 401）。
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.txt", "a"u8.ToArray());

            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/fs/list")
            {
                Content = JsonContent.Create(new { path = "/" }),
            };
            req.Headers.TryAddWithoutValidation("Authorization", "not-a-real-share-code");
            var resp = await client.SendAsync(req);

            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsNotEqualTo(401);
        }
    }

    [Test]
    public async Task Mkdir_Still_Requires_Auth()
    {
        // Write endpoints (mkdir, rename, move, copy, remove, put, ...) must still require auth.
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var resp = await client.PostAsync("/api/fs/mkdir",
                JsonContent.Create(new { path = "/test/newdir" }));
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
    }

    [Test]
    public async Task Remove_Rejects_RelativePathNames()
    {
        // 镜像 Go checkRelativePath：/fs/remove 的 names 只允许纯名称；
        // "../../secret.txt" 会被 UPath 归一化解析后越出 dir 删除兄弟文件，必须 403。
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("secret.txt", "s"u8.ToArray());
            await factory.WriteFileAsync("writable/a.txt", "a"u8.ToArray());

            var loginResp = await client.PostAsync("/api/auth/login",
                JsonContent.Create(new { username = "admin", password = "admin" }));
            var loginJson = await loginResp.Content.ReadFromJsonAsync<JsonDocument>();
            var token = loginJson!.RootElement.GetProperty("data").GetProperty("token").GetString()!;
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);

            foreach (var name in new[] { "../../secret.txt", @"..\..\secret.txt" })
            {
                var resp = await client.PostAsync("/api/fs/remove",
                    JsonContent.Create(new { dir = "/test/writable", names = new[] { name } }));
                var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
                await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(403);
            }

            // 兄弟文件仍在。
            var getResp = await client.PostAsync("/api/fs/get",
                JsonContent.Create(new { path = "/test/secret.txt" }));
            var getJson = await getResp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(getJson!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);
        }
    }

    [Test]
    public async Task List_With_Valid_Token_Resolves_Real_User()
    {
        // When a valid JWT is presented, the user is resolved (not guest).
        // This verifies that AllowAnonymous doesn't break JWT parsing.
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.txt", "a"u8.ToArray());

            // Login as admin first.
            var loginResp = await client.PostAsync("/api/auth/login",
                JsonContent.Create(new { username = "admin", password = "admin" }));
            var loginJson = await loginResp.Content.ReadFromJsonAsync<JsonDocument>();
            var token = loginJson!.RootElement.GetProperty("data").GetProperty("token").GetString()!;
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);

            var resp = await client.PostAsync("/api/fs/list",
                JsonContent.Create(new { path = "/test" }));
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);
        }
    }

    [Test]
    public async Task List_Respects_User_BasePath_Isolation()
    {
        // 镜像 Go：user.JoinPath 把请求路径钉在用户 BasePath 之内，越界相对路径返回 403。
        // 这里验证非 admin 用户的根请求落在自己的 BasePath 内，且看不到兄弟目录。
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("writable/a.txt", "a"u8.ToArray());
            await factory.WriteFileAsync("other/b.txt", "b"u8.ToArray());

            // 以 admin 建一个 BasePath=/test/writable 的普通用户（权限位全开，避免权限位干扰路径断言）。
            client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client, "admin", "admin"));
            var create = await client.PostAsync("/api/admin/user/create", JsonContent.Create(new
            {
                username = "bob",
                password = "pw",
                role = 0,
                base_path = "/test/writable",
                permission = 0x71FF,
            }));
            var createJson = await create.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(createJson!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);

            client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client, "bob", "pw"));

            // 根路径请求被套到 bob 的 BasePath 内：只看得到自己的文件，看不到兄弟目录内容。
            var resp = await client.PostAsync("/api/fs/list", JsonContent.Create(new { path = "/" }));
            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);
            var names = json.RootElement.GetProperty("data").GetProperty("content")
                .EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
            await Assert.That(names.Any(n => n == "a.txt")).IsTrue();
            await Assert.That(names.Any(n => n == "other")).IsFalse();

            // 越界相对路径被拒绝（镜像 Go utils.JoinBasePath → errs.RelativePath）。
            var esc = await client.PostAsync("/api/fs/list", JsonContent.Create(new { path = "../other" }));
            var escJson = await esc.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(escJson!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(403);
        }
    }

    [Test]
    public async Task Management_Endpoints_Deny_User_Without_Permission_Bits()
    {
        // 镜像 Go 各管理端点内联的 user.CanXxx() 检查：无相应权限位的用户一律 403。
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("base/a.txt", "a"u8.ToArray());
            await factory.WriteFileAsync("base/empty/.keep", "k"u8.ToArray());

            client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client, "admin", "admin"));
            await CreateUserAsync(client, "noperm", "pw", permission: 0);
            client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client, "noperm", "pw"));

            var calls = new (string Route, object Body)[]
            {
                ("/api/fs/mkdir", new { path = "/test/base/newdir" }),
                ("/api/fs/rename", new { path = "/test/base/a.txt", name = "z.txt" }),
                ("/api/fs/batch_rename", new { src_dir = "/test/base", rename_objects = new[] { new { src_name = "a.txt", new_name = "bz.txt" } } }),
                ("/api/fs/regex_rename", new { src_dir = "/test/base", src_name_regex = "a", new_name_regex = "b" }),
                ("/api/fs/move", new { src_dir = "/test/base", dst_dir = "/test/base", names = new[] { "a.txt" } }),
                ("/api/fs/copy", new { src_dir = "/test/base", dst_dir = "/test/base", names = new[] { "a.txt" } }),
                ("/api/fs/remove", new { dir = "/test/base", names = new[] { "a.txt" } }),
                ("/api/fs/remove_empty_directory", new { path = "/test/base/empty" }),
                ("/api/fs/recursive_move", new { src_dir = "/test/base", dst_dir = "/test/base" }),
            };

            foreach (var (route, body) in calls)
            {
                var json = await PostJsonAsync(client, route, body);
                var code = json.RootElement.GetProperty("code").GetInt32();
                // 权限门禁先于任何文件操作，故即便路径不存在也应是 403。
                await Assert.That(code).IsEqualTo(403);
            }
        }
    }

    [Test]
    public async Task Management_Endpoints_Allow_User_With_Required_Permission_Bit()
    {
        // 单一位用户的正/负控制：持有该端点所需位可放行（非 403），缺位则 403。
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var cases = new (int Bit, (string Route, object Body) Allowed, (string Route, object Body) Denied)[]
            {
                (1 << 3, ("/api/fs/mkdir", (object)new { path = "/test/base/newdir" }),
                         ("/api/fs/rename", new { path = "/test/base/a.txt", name = "r.txt" })),
                (1 << 4, ("/api/fs/rename", (object)new { path = "/test/base/a.txt", name = "r.txt" }),
                         ("/api/fs/mkdir", new { path = "/test/base/newdir" })),
                (1 << 5, ("/api/fs/move", (object)new { src_dir = "/test/base", dst_dir = "/test/base/moved", names = new[] { "m.txt" } }),
                         ("/api/fs/copy", new { src_dir = "/test/base", dst_dir = "/test/base/moved", names = new[] { "m.txt" } })),
                (1 << 6, ("/api/fs/copy", (object)new { src_dir = "/test/base", dst_dir = "/test/base/copied", names = new[] { "c.txt" } }),
                         ("/api/fs/remove", new { dir = "/test/base", names = new[] { "c.txt" } })),
                (1 << 7, ("/api/fs/remove", (object)new { dir = "/test/base", names = new[] { "d.txt" } }),
                         ("/api/fs/move", new { src_dir = "/test/base", dst_dir = "/test/base/moved", names = new[] { "d.txt" } })),
            };

            var i = 0;
            foreach (var c in cases)
            {
                // 每个 case 前重建夹具，避免上一个 case 的移动/删除影响后续断言。
                await factory.WriteFileAsync("base/a.txt", "a"u8.ToArray());
                await factory.WriteFileAsync("base/m.txt", "m"u8.ToArray());
                await factory.WriteFileAsync("base/c.txt", "c"u8.ToArray());
                await factory.WriteFileAsync("base/d.txt", "d"u8.ToArray());
                await factory.WriteFileAsync("base/moved/.keep", "k"u8.ToArray());
                await factory.WriteFileAsync("base/copied/.keep", "k"u8.ToArray());

                var name = $"bit{i++}";
                client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client, "admin", "admin"));
                await CreateUserAsync(client, name, "pw", c.Bit);
                client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client, name, "pw"));

                var allowed = await PostJsonAsync(client, c.Allowed.Route, c.Allowed.Body);
                // 放行断言只要求绕过权限门禁（非 403）；后续服务层结果不在本测试关注域。
                await Assert.That(allowed.RootElement.GetProperty("code").GetInt32()).IsNotEqualTo(403);
                // move/copy 是任务化端点：200 只代表已入队，文件操作在后台跑。
                // 不等它结束就重建 base/m.txt，会和正在进行的移动撞上
                // （Windows 上直接抛 "file is being used by another process"）。
                if (c.Allowed.Route.EndsWith("/move", StringComparison.Ordinal))
                    await WaitTasksAsync(client, "move");
                else if (c.Allowed.Route.EndsWith("/copy", StringComparison.Ordinal))
                    await WaitTasksAsync(client, "copy");

                var denied = await PostJsonAsync(client, c.Denied.Route, c.Denied.Body);
                await Assert.That(denied.RootElement.GetProperty("code").GetInt32()).IsEqualTo(403);
            }
        }
    }

    [Test]
    public async Task Mkdir_Denied_When_Meta_WriteUsers_Excludes_User()
    {
        // 镜像 Go common.CanWrite：meta 的 write_users 白名单非空、不含该用户且覆盖该路径时拒绝，
        // 即便用户拥有 CanWriteContent 权限位。
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("meta_dir/.keep", "k"u8.ToArray());

            client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client, "admin", "admin"));
            var metaResp = await client.PostAsync("/api/admin/meta/create", JsonContent.Create(new
            {
                path = "/test/meta_dir",
                write = false,
                write_users = new[] { 999L },
                write_users_sub = true,
            }));
            var metaJson = await metaResp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(metaJson!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);

            await CreateUserAsync(client, "metawriter", "pw", permission: 1 << 3);
            client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client, "metawriter", "pw"));

            var json = await PostJsonAsync(client, "/api/fs/mkdir", new { path = "/test/meta_dir/sub" });
            await Assert.That(json.RootElement.GetProperty("code").GetInt32()).IsEqualTo(403);
        }
    }

    [Test]
    public async Task DirectUpload_Info_Applies_Go_Gate()
    {
        // 镜像 Go FsGetDirectUploadInfo 门禁：无 CanWriteContent 位 403；file_name 非纯名称 403；
        // 权限位齐备 + 合法 file_name 则通过门禁（当前无驱动实现直传，服务层回 501，非 403）。
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("base/.keep", "k"u8.ToArray());

            client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client, "admin", "admin"));
            await CreateUserAsync(client, "nowrite", "pw", permission: 0);
            await CreateUserAsync(client, "canwrite", "pw", permission: 1 << 3); // CanWriteContent

            // 无 CanWriteContent 权限位 → 403。
            client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client, "nowrite", "pw"));
            var denied = await PostJsonAsync(client, "/api/fs/get_direct_upload_info",
                new { path = "/test/base", file_name = "a.txt", file_size = 10 });
            await Assert.That(denied.RootElement.GetProperty("code").GetInt32()).IsEqualTo(403);

            // 有权限位但 file_name 含路径成分（/ \、. ..、空）→ 403（镜像 Go checkRelativePath）。
            client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client, "canwrite", "pw"));
            foreach (var bad in new[] { "../evil.txt", "sub/evil.txt", @"..\evil.txt", "", "..", "." })
            {
                var invalid = await PostJsonAsync(client, "/api/fs/get_direct_upload_info",
                    new { path = "/test/base", file_name = bad, file_size = 10 });
                await Assert.That(invalid.RootElement.GetProperty("code").GetInt32()).IsEqualTo(403);
            }

            // 权限位齐备 + 合法 file_name → 通过门禁（非 403）。
            var ok = await PostJsonAsync(client, "/api/fs/get_direct_upload_info",
                new { path = "/test/base", file_name = "a.txt", file_size = 10 });
            await Assert.That(ok.RootElement.GetProperty("code").GetInt32()).IsNotEqualTo(403);
        }
    }

    [Test]
    public async Task Copy_Denied_When_Source_Meta_ReadUsers_Excludes_User()
    {
        // 镜像 Go FsCopy：CanCopy 之外，源目录还要过 common.CanRead（meta 的 read_users 白名单）。
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("copysrc/a.txt", "a"u8.ToArray());
            await factory.WriteFileAsync("plain/a.txt", "a"u8.ToArray());
            await factory.WriteFileAsync("out/.keep", "k"u8.ToArray());

            client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client, "admin", "admin"));
            // 源目录 meta：read_users 仅含 999（非调用者）。
            var metaResp = await client.PostAsync("/api/admin/meta/create", JsonContent.Create(new
            {
                path = "/test/copysrc",
                read_users = new[] { 999L },
                read_users_sub = true,
            }));
            var metaJson = await metaResp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(metaJson!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);

            await CreateUserAsync(client, "copier", "pw", permission: 1 << 6); // CanCopy
            client.DefaultRequestHeaders.Authorization = new("Bearer", await LoginAsync(client, "copier", "pw"));

            // 有 CanCopy 位，但源目录 meta 的 read_users 白名单不含该用户 → 403。
            var denied = await PostJsonAsync(client, "/api/fs/copy",
                new { src_dir = "/test/copysrc", dst_dir = "/test/out", names = new[] { "a.txt" } });
            await Assert.That(denied.RootElement.GetProperty("code").GetInt32()).IsEqualTo(403);

            // 源目录换成无读限制的路径 → 通过读门禁进入服务层（非 403）。
            var ok = await PostJsonAsync(client, "/api/fs/copy",
                new { src_dir = "/test/plain", dst_dir = "/test/out", names = new[] { "a.txt" } });
            await Assert.That(ok.RootElement.GetProperty("code").GetInt32()).IsNotEqualTo(403);
            // 同上：后台复制还在写 out/a.txt 时销毁夹具，删目录会因文件被占用失败。
            await WaitTasksAsync(client, "copy");
        }
    }

    /// <summary>
    /// 等待某类型的后台任务清空。
    /// </summary>
    /// <remarks>
    /// move/copy 是任务化端点：HTTP 200 只代表任务已入队，文件操作在线程池里继续跑。
    /// 测试若在返回后立刻重建/删除同一批文件，就会和还在进行中的任务撞上 ——
    /// Windows 上表现为 <c>The process cannot access the file … because it is being used by another process</c>。
    /// </remarks>
    private static async Task WaitTasksAsync(HttpClient client, string type)
    {
        for (var i = 0; i < 150; i++)
        {
            var resp = await client.GetAsync($"/api/task/{type}/undone");
            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            var data = json!.RootElement.GetProperty("data");
            if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() == 0) return;
            await Task.Delay(20);
        }
    }

    private static async Task<string> LoginAsync(HttpClient client, string username, string password)
    {
        var resp = await client.PostAsync("/api/auth/login", JsonContent.Create(new { username, password }));
        var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        return json!.RootElement.GetProperty("data").GetProperty("token").GetString()!;
    }

    private static async Task CreateUserAsync(HttpClient client, string username, string password, int permission)
    {
        var resp = await client.PostAsync("/api/admin/user/create",
            JsonContent.Create(new { username, password, role = 0, permission }));
        var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);
    }

    private static async Task<JsonDocument> PostJsonAsync(HttpClient client, string route, object body)
    {
        var resp = await client.PostAsync(route, JsonContent.Create(body));
        return (await resp.Content.ReadFromJsonAsync<JsonDocument>())!;
    }
}
