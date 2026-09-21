using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml;
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

    // --- Multipart upload (Go server/s3/multipart.go) ---

    [Test]
    public async Task MultipartUpload_FullFlow_AssemblesFile()
    {
        var (factory, rawClient) = await SetupAsync();
        await using (factory)
        {
            var client = await AuthClientAsync(rawClient);

            // 1) CreateMultipartUpload: POST /s3/{bucket}/{key}?uploads
            var initResp = await client.PostAsync("/s3/test/big.bin?uploads", null);
            await Assert.That(initResp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var uploadId = ExtractXml(await initResp.Content.ReadAsStringAsync(), "UploadId");
            await Assert.That(string.IsNullOrEmpty(uploadId)).IsFalse();

            // 2) UploadPart: PUT ...?partNumber=N&uploadId=X
            var r1 = await client.PutAsync($"/s3/test/big.bin?partNumber=1&uploadId={uploadId}",
                new ByteArrayContent("aaaa"u8.ToArray()));
            await Assert.That(r1.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var etag1 = r1.Headers.ETag!.Tag;

            var r2 = await client.PutAsync($"/s3/test/big.bin?partNumber=2&uploadId={uploadId}",
                new ByteArrayContent("bbbbbb"u8.ToArray()));
            await Assert.That(r2.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var etag2 = r2.Headers.ETag!.Tag;

            // 3) CompleteMultipartUpload: POST ...?uploadId=X with XML part list
            var completeXml =
                "<CompleteMultipartUpload>" +
                $"<Part><PartNumber>1</PartNumber><ETag>{etag1}</ETag></Part>" +
                $"<Part><PartNumber>2</PartNumber><ETag>{etag2}</ETag></Part>" +
                "</CompleteMultipartUpload>";
            var completeResp = await client.PostAsync($"/s3/test/big.bin?uploadId={uploadId}",
                new StringContent(completeXml, Encoding.UTF8, "application/xml"));
            await Assert.That(completeResp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var cxml = await completeResp.Content.ReadAsStringAsync();
            await Assert.That(cxml).Contains("CompleteMultipartUploadResult");
            // multipart etag ends with "-2" (2 parts)
            await Assert.That(ExtractXml(cxml, "ETag")).EndsWith("-2\"");

            // 4) Parts must be concatenated in ascending order
            var getResp = await client.GetAsync("/s3/test/big.bin");
            await Assert.That(getResp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await getResp.Content.ReadAsStringAsync()).IsEqualTo("aaaabbbbbb");
        }
    }

    [Test]
    public async Task MultipartUpload_Complete_Rejects_Wrong_ETag()
    {
        var (factory, rawClient) = await SetupAsync();
        await using (factory)
        {
            var client = await AuthClientAsync(rawClient);
            var initResp = await client.PostAsync("/s3/test/etag.bin?uploads", null);
            var uploadId = ExtractXml(await initResp.Content.ReadAsStringAsync(), "UploadId");

            await client.PutAsync($"/s3/test/etag.bin?partNumber=1&uploadId={uploadId}",
                new ByteArrayContent("data"u8.ToArray()));

            var badXml = "<CompleteMultipartUpload><Part><PartNumber>1</PartNumber>" +
                         "<ETag>\"deadbeef\"</ETag></Part></CompleteMultipartUpload>";
            var resp = await client.PostAsync($"/s3/test/etag.bin?uploadId={uploadId}",
                new StringContent(badXml, Encoding.UTF8, "application/xml"));
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        }
    }

    [Test]
    public async Task MultipartUpload_Abort_Then_Complete_Returns404()
    {
        var (factory, rawClient) = await SetupAsync();
        await using (factory)
        {
            var client = await AuthClientAsync(rawClient);
            var initResp = await client.PostAsync("/s3/test/abort.bin?uploads", null);
            var uploadId = ExtractXml(await initResp.Content.ReadAsStringAsync(), "UploadId");

            var abort = await client.DeleteAsync($"/s3/test/abort.bin?uploadId={uploadId}");
            await Assert.That(abort.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

            var complete = await client.PostAsync($"/s3/test/abort.bin?uploadId={uploadId}",
                new StringContent("<CompleteMultipartUpload/>", Encoding.UTF8, "application/xml"));
            await Assert.That(complete.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        }
    }

    [Test]
    public async Task ListMultipartUploads_ReturnsPendingUpload()
    {
        var (factory, rawClient) = await SetupAsync();
        await using (factory)
        {
            var client = await AuthClientAsync(rawClient);
            var initResp = await client.PostAsync("/s3/test/pending.bin?uploads", null);
            var uploadId = ExtractXml(await initResp.Content.ReadAsStringAsync(), "UploadId");

            var resp = await client.GetAsync("/s3/test?uploads");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var xml = await resp.Content.ReadAsStringAsync();
            await Assert.That(xml).Contains("ListMultipartUploadsResult");
            await Assert.That(xml).Contains(uploadId);
            await Assert.That(xml).Contains("pending.bin");
        }
    }

    // --- ListObjectsV2 / CopyObject / DeleteMulti / Range ---

    [Test]
    public async Task ListObjectsV2_ReturnsKeyCount()
    {
        var (factory, rawClient) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("v2a.txt", "a"u8.ToArray());
            await factory.WriteFileAsync("v2b.txt", "b"u8.ToArray());
            var client = await AuthClientAsync(rawClient);

            var resp = await client.GetAsync("/s3/test?list-type=2");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var xml = await resp.Content.ReadAsStringAsync();
            await Assert.That(xml).Contains("ListBucketResult");
            await Assert.That(xml).Contains("KeyCount");
            await Assert.That(xml).Contains("v2a.txt");
        }
    }

    [Test]
    public async Task CopyObject_DuplicatesFile()
    {
        var (factory, rawClient) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("src.txt", "copy-me"u8.ToArray());
            var client = await AuthClientAsync(rawClient);

            var req = new HttpRequestMessage(HttpMethod.Put, "/s3/test/dst.txt");
            req.Headers.Add("x-amz-copy-source", "/test/src.txt");
            var resp = await client.SendAsync(req);
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await resp.Content.ReadAsStringAsync()).Contains("CopyObjectResult");

            var get = await client.GetAsync("/s3/test/dst.txt");
            await Assert.That(get.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await get.Content.ReadAsStringAsync()).IsEqualTo("copy-me");
        }
    }

    [Test]
    public async Task DeleteMulti_RemovesObjects_AndReportsDeleted()
    {
        var (factory, rawClient) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("d1.txt", "1"u8.ToArray());
            await factory.WriteFileAsync("d2.txt", "2"u8.ToArray());
            var client = await AuthClientAsync(rawClient);

            var xml = "<Delete>" +
                      "<Object><Key>d1.txt</Key></Object>" +
                      "<Object><Key>d2.txt</Key></Object>" +
                      "</Delete>";
            var resp = await client.PostAsync("/s3/test?delete",
                new StringContent(xml, Encoding.UTF8, "application/xml"));
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var body = await resp.Content.ReadAsStringAsync();
            await Assert.That(body).Contains("DeleteResult");
            await Assert.That(body).Contains("d1.txt");

            await Assert.That((await client.GetAsync("/s3/test/d1.txt")).StatusCode)
                .IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That((await client.GetAsync("/s3/test/d2.txt")).StatusCode)
                .IsEqualTo(HttpStatusCode.NotFound);
        }
    }

    [Test]
    public async Task GetObject_WithRange_Returns206_AndPartialBody()
    {
        var (factory, rawClient) = await SetupAsync();
        await using (factory)
        {
            await factory.WriteFileAsync("range.txt", "0123456789"u8.ToArray());
            var client = await AuthClientAsync(rawClient);

            var req = new HttpRequestMessage(HttpMethod.Get, "/s3/test/range.txt");
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2, 5);
            var resp = await client.SendAsync(req);

            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.PartialContent);
            await Assert.That(await resp.Content.ReadAsStringAsync()).IsEqualTo("2345");
            await Assert.That(resp.Content.Headers.ContentRange!.ToString()).IsEqualTo("bytes 2-5/10");
        }
    }

    private static string ExtractXml(string xml, string localName)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        return doc.SelectSingleNode($"//*[local-name()='{localName}']")?.InnerText ?? "";
    }
}
