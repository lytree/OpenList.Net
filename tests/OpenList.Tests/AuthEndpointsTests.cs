using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using OpenList.Core.Interfaces;
using OpenList.Server.Endpoints;
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

/// <summary>
/// SSO 回调 postMessage 目标源钉死逻辑（镜像 Go server/handles/ssologin.go 的 ssoTargetOrigin）。
/// 纯单元测试，不启动 Web 主机，故无需 [NotInParallel]。
/// </summary>
public class SsoTargetOriginTests
{
    private static HttpContext Ctx(string scheme, string host)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = scheme;
        ctx.Request.Host = new HostString(host);
        return ctx;
    }

    [Test]
    public async Task SsoTargetOrigin_Prefers_Valid_Configured_Origin()
    {
        var ctx = Ctx("https", "api.example.com");
        await Assert.That(AuthEndpoints.SsoTargetOrigin(ctx, "https://app.example.com"))
            .IsEqualTo("https://app.example.com");
    }

    [Test]
    public async Task SsoTargetOrigin_Normalizes_Trailing_Slash_And_Ignores_Api_Case()
    {
        var ctx = Ctx("https", "api.example.com");
        // 配置源大小写不敏感、尾斜杠被归一化（Uri.Authority 会小写化 host）。
        await Assert.That(AuthEndpoints.SsoTargetOrigin(ctx, "HTTPS://App.Example.com/"))
            .IsEqualTo("https://app.example.com");
    }

    [Test]
    public async Task SsoTargetOrigin_Rejects_Invalid_Configured_Values()
    {
        var ctx = Ctx("https", "api.example.com");
        // 非法配置一律回退到 API 自身源，绝不原样透传（否则可能钉到攻击者页面）。
        string?[] invalid =
        [
            "https://user:pass@evil.com",   // 含 UserInfo
            "https://evil.com/path",        // 含路径
            "https://evil.com/?q=1",        // 含查询串
            "https://evil.com/#f",          // 含片段
            "javascript:alert(1)",          // 非 http/https 协议
            "ftp://evil.com",               // 非 http/https 协议
            "*",                            // 通配符
            "",
            null,
        ];
        foreach (var v in invalid)
            await Assert.That(AuthEndpoints.SsoTargetOrigin(ctx, v)).IsEqualTo("https://api.example.com");
    }

    [Test]
    public async Task SsoTargetOrigin_Falls_Back_To_Slash_When_Api_Base_Unparseable()
    {
        // 无 scheme/host 的上下文 → 连 API 源都构造不出来，退回 "/"（仅同源可接收）。
        var ctx = new DefaultHttpContext();
        await Assert.That(AuthEndpoints.SsoTargetOrigin(ctx, null)).IsEqualTo("/");
        await Assert.That(AuthEndpoints.SsoTargetOrigin(ctx, "https://user:pass@evil.com")).IsEqualTo("/");
    }

    [Test]
    public async Task RenderSsoCallbackHtml_Pins_Origin_And_Omits_Wildcard()
    {
        var html = AuthEndpoints.RenderSsoCallbackHtml(
            new SsoCallbackResult { Token = "tok-123" }, "https://app.example.com");
        await Assert.That(html).Contains("postMessage");
        await Assert.That(html).Contains("{\"token\":\"tok-123\"}");
        await Assert.That(html).Contains("\"https://app.example.com\"");
        await Assert.That(html).DoesNotContain("\"*\"");
    }

    [Test]
    public async Task RenderSsoCallbackHtml_Emits_SsoId_In_Link_Mode()
    {
        var html = AuthEndpoints.RenderSsoCallbackHtml(
            new SsoCallbackResult { SsoId = "id-42" }, "/");
        await Assert.That(html).Contains("{\"sso_id\":\"id-42\"}");
        await Assert.That(html).Contains("\"/\"");
        await Assert.That(html).DoesNotContain("\"*\"");
    }
}
