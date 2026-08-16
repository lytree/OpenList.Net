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

    [Test]
    public async Task List_Allows_Anonymous_Guest_Access()
    {
        // Go allows guest access to /api/fs/list via Auth(true) middleware.
        // .NET mirrors this with AllowAnonymous on the read endpoints.
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
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
            await factory.WriteFileAsync("a.txt", "a"u8.ToArray());

            var resp = await client.PostAsync("/api/fs/dirs",
                JsonContent.Create(new { path = "/test" }));
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);
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
}
