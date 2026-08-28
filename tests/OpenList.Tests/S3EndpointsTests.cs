using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TUnit.Core;

namespace OpenList.Tests;

/// <summary>
/// Integration tests for S3-compatible gateway endpoints (/s3).
/// Uses TestWebAppFactory with a Local storage mounted at /test.
/// S3 auth: token in the Authorization header is validated via IAuthService and
/// resolved to the real backing user (no more guest-admin bypass).
/// </summary>
[NotInParallel("WebIntegration")]
public class S3EndpointsTests
{
    private static async Task<(TestWebAppFactory factory, HttpClient client)> SetupAsync()
    {
        var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        await factory.SeedStorageAsync();
        return (factory, client);
    }

    /// <summary>Log in as admin and attach a real JWT Bearer token.</summary>
    private static async Task<HttpClient> AuthClientAsync(HttpClient client)
    {
        var login = await client.PostAsync("/api/auth/login",
            JsonContent.Create(new { username = "admin", password = "admin" }));
        var loginJson = await login.Content.ReadFromJsonAsync<JsonDocument>();
        var token = loginJson!.RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    [Test]
    public async Task ListBuckets_ReturnsXml_WithTestBucket()
    {
        var (factory, rawClient) = await SetupAsync();
        await using (factory)
        {
            var client = await AuthClientAsync(rawClient);
            var resp = await client.GetAsync("/s3/");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var xml = await resp.Content.ReadAsStringAsync();
            await Assert.That(xml).Contains("ListAllMyBucketsResult");
            await Assert.That(xml).Contains("test");
        }
    }

    [Test]
    public async Task ListBuckets_Unauthorized_WithoutAuth()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var resp = await client.GetAsync("/s3/");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
    }

    [Test]
    public async Task HeadBucket_ReturnsOk_ForExistingBucket()
    {
        var (factory, rawClient) = await SetupAsync();
        await using (factory)
        {
            var client = await AuthClientAsync(rawClient);
            var req = new HttpRequestMessage(HttpMethod.Head, "/s3/test");
            var resp = await client.SendAsync(req);
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
    }

    [Test]
    public async Task HeadBucket_Returns404_ForMissingBucket()
    {
        var (factory, rawClient) = await SetupAsync();
        await using (factory)
        {
            var client = await AuthClientAsync(rawClient);
            var req = new HttpRequestMessage(HttpMethod.Head, "/s3/nonexistent");
            var resp = await client.SendAsync(req);
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
    }

    [Test]
    public async Task ListObjects_ReturnsXml_WithObjects()
    {
        var (factory, rawClient) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("file1.txt", "hello"u8.ToArray());
            await factory.WriteFileAsync("file2.txt", "world"u8.ToArray());
            var client = await AuthClientAsync(rawClient);
            var resp = await client.GetAsync("/s3/test");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var xml = await resp.Content.ReadAsStringAsync();
            await Assert.That(xml).Contains("ListBucketResult");
            await Assert.That(xml).Contains("file1.txt");
            await Assert.That(xml).Contains("file2.txt");
        }
    }

    [Test]
    public async Task ListObjects_Returns404_ForMissingBucket()
    {
        var (factory, rawClient) = await SetupAsync();
        await using (factory)
        {
            var client = await AuthClientAsync(rawClient);
            var resp = await client.GetAsync("/s3/nonexistent");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
    }

    [Test]
    public async Task PutObject_Then_GetObject_RoundTrip()
    {
        var (factory, rawClient) = await SetupAsync();
        await using (factory)
        {
            var client = await AuthClientAsync(rawClient);
            var putContent = new ByteArrayContent("s3-upload"u8.ToArray());
            var putResp = await client.PutAsync("/s3/test/uploaded.txt", putContent);
            await Assert.That(putResp.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var getResp = await client.GetAsync("/s3/test/uploaded.txt");
            await Assert.That(getResp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var body = await getResp.Content.ReadAsStringAsync();
            await Assert.That(body).IsEqualTo("s3-upload");
        }
    }

    [Test]
    public async Task HeadObject_ReturnsMetadata()
    {
        var (factory, rawClient) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("meta.txt", "metadata"u8.ToArray());
            var client = await AuthClientAsync(rawClient);
            var req = new HttpRequestMessage(HttpMethod.Head, "/s3/test/meta.txt");
            var resp = await client.SendAsync(req);
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(resp.Headers.Contains("ETag")).IsTrue();
        }
    }

    [Test]
    public async Task GetObject_Returns404_ForMissingKey()
    {
        var (factory, rawClient) = await SetupAsync();
        await using (factory)
        {
            var client = await AuthClientAsync(rawClient);
            var resp = await client.GetAsync("/s3/test/nonexistent.txt");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
    }

    [Test]
    public async Task DeleteObject_RemovesFile()
    {
        var (factory, rawClient) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("delete-me.txt", "bye"u8.ToArray());
            var client = await AuthClientAsync(rawClient);

            var delResp = await client.DeleteAsync("/s3/test/delete-me.txt");
            await Assert.That(delResp.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

            var getResp = await client.GetAsync("/s3/test/delete-me.txt");
            await Assert.That(getResp.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
    }

    [Test]
    public async Task GarbageToken_ReturnsUnauthorized()
    {
        // Auth hardening: a syntactically-present but invalid token must NOT be
        // accepted as a guest admin (previous behavior). It is rejected at the gateway.
        var (factory, rawClient) = await SetupAsync();
        await using (factory)
        {
            rawClient.DefaultRequestHeaders.Authorization = new("Bearer", "not-a-real-jwt");
            var resp = await rawClient.GetAsync("/s3/");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
    }

    [Test]
    public async Task AwsSigv4StyleHeader_With_ValidToken_Works()
    {
        // The access key in a SigV4-style Authorization header is treated as the JWT token.
        // A real token placed there must resolve to the backing user and succeed.
        var (factory, rawClient) = await SetupAsync();
        await using (factory)
        {
            var login = await rawClient.PostAsync("/api/auth/login",
                JsonContent.Create(new { username = "admin", password = "admin" }));
            var loginJson = await login.Content.ReadFromJsonAsync<JsonDocument>();
            var token = loginJson!.RootElement.GetProperty("data").GetProperty("token").GetString()!;
            rawClient.DefaultRequestHeaders.Authorization =
                new("AWS4-HMAC-SHA256", $"Credential={token}/20260816/us-east-1/s3/aws4_request, " +
                                        "SignedHeaders=x-amz-date, Signature=deadbeef");

            var resp = await rawClient.GetAsync("/s3/");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
    }
}
