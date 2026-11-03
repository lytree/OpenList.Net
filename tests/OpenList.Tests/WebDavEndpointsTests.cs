using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Interfaces;
using TUnit.Core;

namespace OpenList.Tests;

/// <summary>
/// Integration tests for WebDAV endpoints (/dav).
/// Uses TestWebAppFactory with a Local storage mounted at /test.
/// WebDAV auth: HTTP Basic auth with admin:admin (seeded by DbInitializer).
/// </summary>
// 这一类的用例曾记录过「首个 WebDAV 请求无限挂起，拖死整个套件」，于是有一条 DELETE 用例被
// [Skip] 掉了。2026-11-01 复核：真实服务器上 OPTIONS/PUT/GET/PROPFIND/DELETE 全部毫秒级返回
// （DELETE 204 + 磁盘文件确实消失 + 事后 GET 404），套件里连跑 3 轮 16/16 也全绿 —— 说明那次
// 挂起不是产品缺陷，[Skip] 已摘。这里补一道超时：真挂了要快速失败，而不是挂住整轮 CI。
[Timeout(60_000)]
[NotInParallel("WebIntegration")]
public class WebDavEndpointsTests
{
    private static async Task<(TestWebAppFactory factory, HttpClient client)> SetupAsync()
    {
        var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        await factory.SeedStorageAsync();
        client.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String("admin:admin"u8.ToArray()));
        return (factory, client);
    }

    [Test]
    public async Task Options_ReturnsDavCapabilities(CancellationToken ct)
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var req = new HttpRequestMessage(HttpMethod.Options, "/dav/test");
            var resp = await client.SendAsync(req);
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(resp.Headers.Contains("DAV")).IsTrue();
            var dav = resp.Headers.GetValues("DAV").First();
            await Assert.That(dav).Contains("1");
            await Assert.That(dav).Contains("2");
        }
    }

    [Test]
    public async Task Propfind_Depth0_ReturnsSelfOnly(CancellationToken ct)
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("a.txt", "a"u8.ToArray());
            await factory.WriteFileAsync("b.txt", "b"u8.ToArray());

            var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/dav/test");
            req.Headers.Add("Depth", "0");
            var resp = await client.SendAsync(req);
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.MultiStatus);
            var xml = await resp.Content.ReadAsStringAsync();
            await Assert.That(xml).Contains("multistatus");
            // Depth 0 = only the directory itself, not children
            await Assert.That(xml).Contains("/dav/test");
        }
    }

    [Test]
    public async Task Propfind_Depth1_ReturnsChildren(CancellationToken ct)
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("child1.txt", "1"u8.ToArray());
            await factory.WriteFileAsync("child2.txt", "2"u8.ToArray());

            var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/dav/test");
            req.Headers.Add("Depth", "1");
            var resp = await client.SendAsync(req);
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.MultiStatus);
            var xml = await resp.Content.ReadAsStringAsync();
            await Assert.That(xml).Contains("child1.txt");
            await Assert.That(xml).Contains("child2.txt");
        }
    }

    [Test]
    public async Task Get_ReturnsFileContent(CancellationToken ct)
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("download.txt", "webdav-content"u8.ToArray());
            var resp = await client.GetAsync("/dav/test/download.txt");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var body = await resp.Content.ReadAsStringAsync();
            await Assert.That(body).IsEqualTo("webdav-content");
        }
    }

    [Test]
    public async Task Get_Returns404_ForMissingFile(CancellationToken ct)
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var resp = await client.GetAsync("/dav/test/nonexistent.txt");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
    }

    [Test]
    public async Task Head_ReturnsHeaders_ForFile(CancellationToken ct)
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("head.txt", "head-test"u8.ToArray());
            var req = new HttpRequestMessage(HttpMethod.Head, "/dav/test/head.txt");
            var resp = await client.SendAsync(req);
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(resp.Content.Headers.Contains("Content-Length")).IsTrue();
        }
    }

    [Test]
    public async Task Put_Then_Get_RoundTrip(CancellationToken ct)
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var content = new ByteArrayContent("dav-upload"u8.ToArray());
            var putResp = await client.PutAsync("/dav/test/uploaded.txt", content);
            await Assert.That(putResp.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var getResp = await client.GetAsync("/dav/test/uploaded.txt");
            await Assert.That(getResp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var body = await getResp.Content.ReadAsStringAsync();
            await Assert.That(body).IsEqualTo("dav-upload");
        }
    }

    [Test]
    public async Task Mkcol_CreatesDirectory(CancellationToken ct)
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var req = new HttpRequestMessage(new HttpMethod("MKCOL"), "/dav/test/newdir");
            var resp = await client.SendAsync(req);
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);

            // Verify via PROPFIND
            var pfReq = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/dav/test/newdir");
            pfReq.Headers.Add("Depth", "0");
            var pfResp = await client.SendAsync(pfReq);
            await Assert.That(pfResp.StatusCode).IsEqualTo(HttpStatusCode.MultiStatus);
        }
    }

    [Test]
    public async Task Delete_RemovesFile(CancellationToken ct)
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("delete.txt", "x"u8.ToArray());
            var delResp = await client.DeleteAsync("/dav/test/delete.txt");
            await Assert.That(delResp.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

            var getResp = await client.GetAsync("/dav/test/delete.txt");
            await Assert.That(getResp.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
    }

    [Test]
    public async Task Unauthorized_WithoutBasicAuth(CancellationToken ct)
    {
        var (factory, _) = await SetupAsync();
        await using (factory)
        {
            var client = factory.CreateClient(); // no auth header
            var resp = await client.GetAsync("/dav/test");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
    }

    // --- LOCK / UNLOCK (in-memory lock store) ---

    [Test]
    public async Task Lock_ReturnsToken_And_LockDiscovery(CancellationToken ct)
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("lockable.txt", "x"u8.ToArray());

            var req = new HttpRequestMessage(new HttpMethod("LOCK"), "/dav/test/lockable.txt");
            req.Headers.Add("Timeout", "Second-60");
            var resp = await client.SendAsync(req);
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.MultiStatus);
            await Assert.That(resp.Headers.Contains("Lock-Token")).IsTrue();
            var xml = await resp.Content.ReadAsStringAsync();
            await Assert.That(xml).Contains("activelock");
            await Assert.That(xml).Contains("exclusive");
            await Assert.That(xml).Contains("Second-60");
        }
    }

    [Test]
    public async Task Lock_Twice_SamePath_Returns423Locked(CancellationToken ct)
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("conflict.txt", "x"u8.ToArray());

            var first = new HttpRequestMessage(new HttpMethod("LOCK"), "/dav/test/conflict.txt");
            var firstResp = await client.SendAsync(first);
            await Assert.That(firstResp.StatusCode).IsEqualTo(HttpStatusCode.MultiStatus);

            var second = new HttpRequestMessage(new HttpMethod("LOCK"), "/dav/test/conflict.txt");
            var secondResp = await client.SendAsync(second);
            await Assert.That(secondResp.StatusCode).IsEqualTo(HttpStatusCode.Locked); // 423
        }
    }

    [Test]
    public async Task Unlock_WithMatchingToken_Returns204(CancellationToken ct)
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("unlock.txt", "x"u8.ToArray());

            var lockReq = new HttpRequestMessage(new HttpMethod("LOCK"), "/dav/test/unlock.txt");
            var lockResp = await client.SendAsync(lockReq);
            var token = lockResp.Headers.GetValues("Lock-Token").First().Trim('<', '>');

            var unlockReq = new HttpRequestMessage(new HttpMethod("UNLOCK"), "/dav/test/unlock.txt");
            unlockReq.Headers.Add("Lock-Token", $"<{token}>");
            var unlockResp = await client.SendAsync(unlockReq);
            await Assert.That(unlockResp.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

            // After unlock, a new lock should succeed.
            var relockResp = await client.SendAsync(new HttpRequestMessage(new HttpMethod("LOCK"), "/dav/test/unlock.txt"));
            await Assert.That(relockResp.StatusCode).IsEqualTo(HttpStatusCode.MultiStatus);
        }
    }

    [Test]
    public async Task Unlock_WithMismatchedToken_Returns409(CancellationToken ct)
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("mismatch.txt", "x"u8.ToArray());

            var lockReq = new HttpRequestMessage(new HttpMethod("LOCK"), "/dav/test/mismatch.txt");
            await client.SendAsync(lockReq);

            var unlockReq = new HttpRequestMessage(new HttpMethod("UNLOCK"), "/dav/test/mismatch.txt");
            unlockReq.Headers.Add("Lock-Token", "<opaquelocktoken:wrong-token>");
            var unlockResp = await client.SendAsync(unlockReq);
            await Assert.That(unlockResp.StatusCode).IsEqualTo(HttpStatusCode.Conflict); // 409
        }
    }

    [Test]
    public async Task Unlock_WithoutLockTokenHeader_Returns400(CancellationToken ct)
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var req = new HttpRequestMessage(new HttpMethod("UNLOCK"), "/dav/test/anything.txt");
            var resp = await client.SendAsync(req);
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }
    }

    [Test]
    public async Task Lock_Unauthorized_WithoutAuth(CancellationToken ct)
    {
        var (factory, _) = await SetupAsync();
        await using (factory)
        {
            var client = factory.CreateClient(); // no auth
            var req = new HttpRequestMessage(new HttpMethod("LOCK"), "/dav/test/x.txt");
            var resp = await client.SendAsync(req);
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
    }

    // --- 写路径的守卫（2026-11-01）---
    //
    // 背景：这 5 个写方法原本只校验「已登录、未禁用」，既没有 /api/fs/* 上的权限位校验，
    // 也没有 BasePath 越界检查；DELETE 对不存在的路径还会静默返回 204。下面按行为钉住。

    [Test]
    public async Task Delete_MissingFile_ReturnsNotFound(CancellationToken ct)
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            // RFC 4918 §9.6：删不存在的资源是 404，不是「删成功了」。
            var resp = await client.DeleteAsync("/dav/test/never-existed.txt");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
    }

    [Test]
    public async Task Delete_WithoutRemovePermission_IsForbidden(CancellationToken ct)
    {
        var (factory, _) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("guarded.txt", "x"u8.ToArray());

            // permission=0：没有任何写/删权限的普通用户
            using (var scope = factory.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<IUserService>();
                var created = await users.CreateAsync(new CreateUserReq
                {
                    Username = "reader",
                    Password = "reader123",
                    Permission = 0,
                });
                await Assert.That(created.Code).IsEqualTo(200);
            }

            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String("reader:reader123"u8.ToArray()));
            var resp = await client.DeleteAsync("/dav/test/guarded.txt");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);

            // 文件必须还在（拒绝要发生在动作之前，而不是删到一半才报错）
            await Assert.That(File.Exists(factory.ResolvePath("guarded.txt"))).IsTrue();
        }
    }

    [Test]
    public async Task Copy_ToSameDirectory_HonoursDestinationName(CancellationToken ct)
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("orig.txt", "copy-me"u8.ToArray());

            // 曾经这里会 400：handler 只取 Destination 的**目录**，再用源名字拼回去，
            // 于是「同目录复制并改名」算出的源和目标完全相同 → Windows 报 used by another process。
            var req = new HttpRequestMessage(new HttpMethod("COPY"), "/dav/test/orig.txt");
            req.Headers.Add("Destination", "/dav/test/dup.txt");
            var resp = await client.SendAsync(req);
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.Created);

            await Assert.That(File.Exists(factory.ResolvePath("dup.txt"))).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(factory.ResolvePath("dup.txt"))).IsEqualTo("copy-me");
            // 源文件不能被覆盖成副本
            await Assert.That(await File.ReadAllTextAsync(factory.ResolvePath("orig.txt"))).IsEqualTo("copy-me");
        }
    }

    [Test]
    public async Task Copy_ToOtherDirectory_UsesDestinationName(CancellationToken ct)
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("orig.txt", "abc"u8.ToArray());
            Directory.CreateDirectory(factory.ResolvePath("dest"));

            var req = new HttpRequestMessage(new HttpMethod("COPY"), "/dav/test/orig.txt");
            req.Headers.Add("Destination", "/dav/test/dest/renamed.txt");
            var resp = await client.SendAsync(req);
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.Created);

            // 目标目录里必须是请求的名字，而不是源文件名 orig.txt
            await Assert.That(File.Exists(factory.ResolvePath("dest/renamed.txt"))).IsTrue();
            await Assert.That(File.Exists(factory.ResolvePath("dest/orig.txt"))).IsFalse();
        }
    }

    [Test]
    public async Task Copy_OverwriteFalse_OnExistingTarget_Returns412(CancellationToken ct)
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("orig.txt", "abc"u8.ToArray());
            await factory.WriteFileAsync("exists.txt", "old"u8.ToArray());

            var req = new HttpRequestMessage(new HttpMethod("COPY"), "/dav/test/orig.txt");
            req.Headers.Add("Destination", "/dav/test/exists.txt");
            req.Headers.Add("Overwrite", "F");
            var resp = await client.SendAsync(req);
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.PreconditionFailed);
            await Assert.That(await File.ReadAllTextAsync(factory.ResolvePath("exists.txt"))).IsEqualTo("old");
        }
    }
}
