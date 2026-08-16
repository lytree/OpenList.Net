using System.Net;
using System.Text;
using TUnit.Core;

namespace OpenList.Tests;

/// <summary>
/// Integration tests for WebDAV endpoints (/dav).
/// Uses TestWebAppFactory with a Local storage mounted at /test.
/// WebDAV auth: HTTP Basic auth with admin:admin (seeded by DbInitializer).
/// </summary>
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
    public async Task Options_ReturnsDavCapabilities()
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
    public async Task Propfind_Depth0_ReturnsSelfOnly()
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
    public async Task Propfind_Depth1_ReturnsChildren()
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
    public async Task Get_ReturnsFileContent()
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
    public async Task Get_Returns404_ForMissingFile()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var resp = await client.GetAsync("/dav/test/nonexistent.txt");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
    }

    [Test]
    public async Task Head_ReturnsHeaders_ForFile()
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
    public async Task Put_Then_Get_RoundTrip()
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
    public async Task Mkcol_CreatesDirectory()
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
    public async Task Delete_RemovesFile()
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
    public async Task Unauthorized_WithoutBasicAuth()
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
    public async Task Lock_ReturnsToken_And_LockDiscovery()
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
    public async Task Lock_Twice_SamePath_Returns423Locked()
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
    public async Task Unlock_WithMatchingToken_Returns204()
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
    public async Task Unlock_WithMismatchedToken_Returns409()
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
    public async Task Unlock_WithoutLockTokenHeader_Returns400()
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
    public async Task Lock_Unauthorized_WithoutAuth()
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
}
