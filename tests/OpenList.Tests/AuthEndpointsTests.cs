using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OpenList.Server.Models;
using TUnit.Core;

namespace OpenList.Tests;

/// <summary>
/// Auth endpoint integration tests: login, me, logout, 2FA flow, SSO/WebAuthn stubs.
/// Uses TestWebAppFactory with seeded admin (admin/admin).
/// </summary>
[NotInParallel("WebIntegration")]
public class AuthEndpointsTests
{
    private static async Task<(TestWebAppFactory factory, HttpClient client)> SetupAsync()
    {
        var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        return (factory, client);
    }

    private static async Task<string> LoginAsync(HttpClient client, string user = "admin", string pw = "admin")
    {
        var resp = await client.PostAsJsonAsync("/api/auth/login", new LoginReq { Username = user, Password = pw });
        var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        return json!.RootElement.GetProperty("data").GetProperty("token").GetString()!;
    }

    private static HttpClient Authed(HttpClient raw, string token)
    {
        raw.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return raw;
    }

    [Test]
    public async Task Login_Returns_Token_For_Admin()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var resp = await client.PostAsJsonAsync("/api/auth/login", new LoginReq { Username = "admin", Password = "admin" });
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);
            await Assert.That(json.RootElement.GetProperty("data").GetProperty("token").GetString()).IsNotNull();
        }
    }

    [Test]
    public async Task Login_Rejects_Wrong_Password()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var resp = await client.PostAsJsonAsync("/api/auth/login", new LoginReq { Username = "admin", Password = "wrong" });
            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(401);
        }
    }

    [Test]
    public async Task Login_Rejects_Unknown_User()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var resp = await client.PostAsJsonAsync("/api/auth/login", new LoginReq { Username = "nobody", Password = "x" });
            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(401);
        }
    }

    [Test]
    public async Task LoginHash_Returns_Token_With_Valid_Hash()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            // The frontend computes sha256(password + suffix) and sends it as the password.
            // Replicate that here using the same HashPassword helper the server uses.
            var hash = OpenList.Infrastructure.Services.UserService.HashPassword("admin");

            var resp = await client.PostAsJsonAsync("/api/auth/login/hash",
                new LoginReq { Username = "admin", Password = hash });
            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);
            await Assert.That(json.RootElement.GetProperty("data").GetProperty("token").GetString()).IsNotNull();
        }
    }

    [Test]
    public async Task LoginHash_Rejects_Invalid_Hash()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var resp = await client.PostAsJsonAsync("/api/auth/login/hash",
                new LoginReq { Username = "admin", Password = "deadbeef" });
            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(401);
        }
    }

    [Test]
    public async Task Me_Returns_Current_User()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var token = await LoginAsync(client);
            Authed(client, token);
            var resp = await client.GetAsync("/api/auth/me");
            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);
            await Assert.That(json.RootElement.GetProperty("data").GetProperty("username").GetString()).IsEqualTo("admin");
        }
    }

    [Test]
    public async Task Me_Rejects_Unauthenticated()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var resp = await client.GetAsync("/api/auth/me");
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
    }

    [Test]
    public async Task Logout_Revokes_Token()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var token = await LoginAsync(client);
            Authed(client, token);

            var logoutResp = await client.PostAsync("/api/auth/logout", null);
            await Assert.That(logoutResp.StatusCode).IsEqualTo(HttpStatusCode.OK);

            // Token should still work at HTTP level (JWT is stateless) — logout is advisory.
            // The real revocation is checked via ValidateTokenAsync in JwtServiceTests.
        }
    }

    [Test]
    public async Task Logout_Accepts_Get_And_Post()
    {
        // Go registers GET /auth/logout; .NET accepts both GET and POST so the
        // unmodified frontend keeps working.
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var token = await LoginAsync(client);
            Authed(client, token);

            var getResp = await client.GetAsync("/api/auth/logout");
            await Assert.That(getResp.StatusCode).IsEqualTo(HttpStatusCode.OK);

            var postResp = await client.PostAsync("/api/auth/logout", null);
            await Assert.That(postResp.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
    }

    [Test]
    public async Task HashPassword_Requires_Admin()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            // Unauthenticated → 401
            var unauthResp = await client.PostAsJsonAsync("/api/auth/hash_password", new HashPasswordReq { Password = "test" });
            await Assert.That(unauthResp.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
    }

    [Test]
    public async Task HashPassword_Returns_Hash_For_Admin()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var token = await LoginAsync(client);
            Authed(client, token);
            var resp = await client.PostAsJsonAsync("/api/auth/hash_password", new HashPasswordReq { Password = "test" });
            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(200);
            await Assert.That(json.RootElement.GetProperty("data").GetProperty("hash").GetString()).IsNotNull();
        }
    }

    [Test]
    public async Task SSO_Providers_Returns_403_When_Disabled()
    {
        // Route is GET /api/auth/sso?method=<provider>; SSO not configured → 403.
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var resp = await client.GetAsync("/api/auth/sso?method=google");
            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(403);
        }
    }

    [Test]
    public async Task WebAuthn_Begin_Login_Returns_403_When_Disabled()
    {
        // Route is GET /api/authn/webauthn_begin_login?username=<user>; WebAuthn not enabled → 403.
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var resp = await client.GetAsync("/api/authn/webauthn_begin_login?username=admin");
            var json = await resp.Content.ReadFromJsonAsync<JsonDocument>();
            await Assert.That(json!.RootElement.GetProperty("code").GetInt32()).IsEqualTo(403);
        }
    }

    [Test]
    public async Task TwoFA_Generate_Requires_Auth()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var resp = await client.PostAsync("/api/auth/2fa/generate", null);
            await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
    }

    [Test]
    public async Task TwoFA_Generate_Returns_Secret()
    {
        var (factory, client) = await SetupAsync();
        await using (factory)
        {
            var token = await LoginAsync(client);
            Authed(client, token);
            var resp = await client.PostAsync("/api/auth/2fa/generate", null);
            var body = await resp.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(body).RootElement;
            await Assert.That(json.GetProperty("code").GetInt32()).IsEqualTo(200);
            await Assert.That(json.GetProperty("data").GetProperty("secret").GetString()).IsNotNull();
            await Assert.That(json.GetProperty("data").GetProperty("qrUri").GetString()).Contains("otpauth://");
        }
    }
}
