using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using OtpNet;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// JWT 签发/校验 + TOTP 双因子认证。Token 采用 HS256 算法，传输时不带 <c>"Bearer "</c> 前缀。
/// 登出使用内存中的已吊销 Token 集合（Phase 16 将替换为分布式缓存）。对应 Go 参考实现
/// <c>internal/op/auth.go</c>。
/// </summary>
public sealed class JwtService : IAuthService
{
    private readonly IUserService _users;
    private readonly AppConfiguration _config;
    private readonly TimeProvider _time;
    private readonly ILdapAuthenticator? _ldap;
    private readonly UserTokenCutoffRegistry? _cutoffs;
    // 必须是进程级单例（DI 注入），否则登出只影响当前请求的作用域 —— 详见 RevokedTokenRegistry。
    private readonly RevokedTokenRegistry _revoked;

    /// <summary>
    /// 创建 JWT 服务实例。
    /// </summary>
    /// <param name="users">用户服务（用于登录校验与双因子信息读取）。</param>
    /// <param name="config">应用配置（读取密钥与 Token 有效期）。</param>
    /// <param name="time">时间源（可选，默认系统时区）。</param>
    /// <param name="ldap">LDAP 认证器（可选，未配置时 LDAP 登录不可用）。</param>
    /// <param name="cutoffs">token 失效登记表（可选；用于 BasePath/权限位变更后即时失效既有 token）。</param>
    /// <param name="revokedTokens">已吊销 token 登记表（可选；生产环境注入进程级单例）。</param>
    public JwtService(IUserService users, AppConfiguration config, TimeProvider? time = null,
        ILdapAuthenticator? ldap = null, UserTokenCutoffRegistry? cutoffs = null,
        RevokedTokenRegistry? revokedTokens = null)
    {
        _users = users;
        _config = config;
        _time = time ?? TimeProvider.System;
        _ldap = ldap;
        _cutoffs = cutoffs;
        _revoked = revokedTokens ?? new RevokedTokenRegistry();
    }

    /// <summary>
    /// 使用用户名/密码（若启用双因子则还需 OTP 码）登录并签发 Token。
    /// </summary>
    /// <param name="username">用户名。</param>
    /// <param name="password">明文密码。</param>
    /// <param name="otpCode">TOTP 验证码（双因子启用时必填）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回 JWT Token；失败返回对应错误（401/403/402）。</returns>
    public async Task<Resp<string>> LoginAsync(string username, string password, string? otpCode = null, CancellationToken ct = default)
    {
        var r = await _users.GetByNameAsync(username, ct);
        if (!r.IsSuccess()) return Resp<string>.Fail("wrong username or password", RespCodes.Unauthorized);
        var u = r.Data!;
        if (u.Disabled) return Resp<string>.Fail("user disabled", RespCodes.Forbidden);
        if (!UserService.VerifyPassword(password, u.PasswordHash))
            return Resp<string>.Fail("wrong username or password", RespCodes.Unauthorized);

        if (u.TwoFactorEnabled)
        {
            if (string.IsNullOrEmpty(otpCode)) return Resp<string>.Fail("2fa required", 402);
            if (!VerifyOtp(u.TwoFactorSecret!, otpCode))
                return Resp<string>.Fail("invalid 2fa code", RespCodes.Unauthorized);
        }
        return Resp<string>.Ok(IssueToken(u));
    }

    /// <summary>
    /// 以已哈希密码进行登录（前端在已有哈希时使用，如 SSO 桥接场景）。
    /// </summary>
    /// <param name="username">用户名。</param>
    /// <param name="passwordHash">哈希后的密码。</param>
    /// <param name="otpCode">TOTP 验证码（双因子启用时必填）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回 JWT Token。</returns>
    public Task<Resp<string>> LoginHashAsync(string username, string passwordHash, string? otpCode = null, CancellationToken ct = default)
    {
        // 哈希登录用于前端已持有哈希密码的场景（如 SSO 桥接），此处直接接受。
        return LoginWithHashInternalAsync(username, passwordHash, otpCode, ct);
    }

    /// <summary>
    /// 哈希密码登录的实现：校验用户、密码哈希与可选双因子，随后签发 Token。
    /// </summary>
    private async Task<Resp<string>> LoginWithHashInternalAsync(string username, string passwordHash, string? otpCode, CancellationToken ct)
    {
        var r = await _users.GetByNameAsync(username, ct);
        if (!r.IsSuccess()) return Resp<string>.Fail("wrong username or password", RespCodes.Unauthorized);
        var u = r.Data!;
        if (u.Disabled) return Resp<string>.Fail("user disabled", RespCodes.Forbidden);
        if (!VerifyHash(u.PasswordHash, passwordHash))
            return Resp<string>.Fail("wrong username or password", RespCodes.Unauthorized);

        if (u.TwoFactorEnabled)
        {
            if (string.IsNullOrEmpty(otpCode)) return Resp<string>.Fail("2fa required", 402);
            if (!VerifyOtp(u.TwoFactorSecret!, otpCode))
                return Resp<string>.Fail("invalid 2fa code", RespCodes.Unauthorized);
        }
        return Resp<string>.Ok(IssueToken(u));
    }

    /// <summary>
    /// 使用常量时间比较两个十六进制密码哈希；长度不一致时返回 false 而非抛出异常。
    /// </summary>
    private static bool VerifyHash(string storedHash, string providedHash)
    {
        if (string.IsNullOrEmpty(storedHash) || string.IsNullOrEmpty(providedHash)) return false;
        if (storedHash.Length != providedHash.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(storedHash),
            System.Text.Encoding.UTF8.GetBytes(providedHash));
    }

    /// <summary>
    /// 通过 LDAP 登录：先由认证器向目录服务校验凭据，成功后匹配<em>已存在</em>的本地账号。
    /// 单用户产品不做自动开通 —— 目录用户必须已经在本机存在（由安装向导或初始密码创建的
    /// 唯一账号），否则返回 401。LDAP 因此只是一种认证方式，不会产生新账号。
    /// </summary>
    /// <param name="username">用户名。</param>
    /// <param name="password">密码。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回 JWT Token；LDAP 未配置时返回 501。</returns>
    public async Task<Resp<string>> LoginLdapAsync(string username, string password, CancellationToken ct = default)
    {
        if (_ldap is null || !await _ldap.IsEnabledAsync(ct))
            return Resp<string>.Fail("ldap not configured", RespCodes.NotImplemented);

        var ok = await _ldap.AuthenticateAsync(username, password, ct);
        if (!ok) return Resp<string>.Fail("wrong username or password", RespCodes.Unauthorized);

        var r = await _users.GetByNameAsync(username, ct);
        // 不再自动开通：目录里的任意用户都能凭空生成本地账号，等于退回多用户模型。
        if (!r.IsSuccess())
            return Resp<string>.Fail("user not registered", RespCodes.Unauthorized);

        var u = r.Data!;
        if (u.Disabled) return Resp<string>.Fail("user disabled", RespCodes.Forbidden);
        return Resp<string>.Ok(IssueToken(u));
    }

    /// <summary>
    /// 生成 TOTP 双因子密钥并持久化（此时双因子仍处于关闭状态，需 Verify2FaAsync 校验通过后才启用）。
    /// </summary>
    /// <param name="userId">用户 Id。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>返回 Base32 格式的双因子密钥。</returns>
    public async Task<Resp<string>> Generate2FaAsync(long userId, CancellationToken ct = default)
    {
        var r = await _users.GetAsync(userId, ct);
        if (!r.IsSuccess()) return Resp<string>.Fail("user not found", RespCodes.NotFound);
        var secret = GenerateOtpSecret();
        // 持久化密钥；双因子在 Verify2FaAsync 确认有效验证码前保持关闭。
        await _users.SetTwoFactorSecretAsync(userId, secret, ct);
        return Resp<string>.Ok(secret);
    }

    /// <summary>
    /// 校验 TOTP 验证码并启用双因子：验证码有效则将用户的双因子置为启用状态。
    /// </summary>
    /// <param name="userId">用户 Id。</param>
    /// <param name="code">TOTP 验证码。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<Resp> Verify2FaAsync(long userId, string code, CancellationToken ct = default)
    {
        var r = await _users.GetAsync(userId, ct);
        if (!r.IsSuccess()) return Resp.Fail("user not found", RespCodes.NotFound);
        var u = r.Data!;
        if (string.IsNullOrEmpty(u.TwoFactorSecret))
            return Resp.Fail("2fa not initialized", RespCodes.BadRequest);
        if (!VerifyOtp(u.TwoFactorSecret, code))
            return Resp.Fail("invalid 2fa code", RespCodes.Unauthorized);

        await _users.UpdateAsync(userId, new UpdateUserReq { TwoFactorEnabled = true }, ct);
        return Resp.Ok();
    }

    /// <summary>
    /// 登出：将指定 Token 登记到进程级已吊销表，使其后续无法通过校验。
    /// </summary>
    public Task<Resp> LogoutAsync(string token, CancellationToken ct = default)
    {
        _revoked.Revoke(token, RevokedTokenRegistry.ResolveExpiry(token, TimeSpan.FromHours(_config.TokenExpiresIn)));
        return Task.FromResult(Resp.Ok());
    }

    /// <summary>
    /// 校验 Token 是否有效：先检查是否已吊销，再验证签名与有效期。
    /// </summary>
    /// <param name="token">JWT Token。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>有效时返回 Token 中声明的用户 Id，否则返回 null。</returns>
    public Task<long?> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        if (_revoked.IsRevoked(token)) return Task.FromResult<long?>(null);
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var parameters = BuildValidationParameters();
            var principal = handler.ValidateToken(token, parameters, out var validated);
            var idClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!long.TryParse(idClaim, out var id)) return Task.FromResult<long?>(null);

            // 账号被禁用后，早于截止点签发的 token 视为失效（详见 UserTokenCutoffRegistry）：
            // 走这条入口的调用方（如 S3 网关）同样必须看到最新状态。
            if (_cutoffs is not null && _cutoffs.IsStale(id, validated!.ValidFrom))
                return Task.FromResult<long?>(null);

            return Task.FromResult<long?>(id);
        }
        catch
        {
            return Task.FromResult<long?>(null);
        }
    }

    /// <summary>
    /// 为指定用户签发一个 HS256 签名、带用户身份声明的 JWT Token。
    /// </summary>
    /// <param name="user">登录用户。</param>
    /// <returns>签发的 Token 字符串。</returns>
    public string IssueToken(User user)
    {
        var now = _time.GetUtcNow();
        var expires = now.AddHours(_config.TokenExpiresIn);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            // jti：同一秒内为同一用户重复签发时，其它 claim 完全相同、JWT 字符串也会一模一样，
            // 于是「登出后立刻重新登录」拿回的还是被吊销的那串 token；带一个随机 id 才能保证每次签发唯一。
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        var creds = new SigningCredentials(BuildSymmetricKey(_config.JwtSecret), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// 基于当前配置构建 Token 校验参数。
    /// </summary>
    public TokenValidationParameters BuildValidationParameters()
        => BuildValidationParameters(_config.JwtSecret);

    /// <summary>静态辅助方法，供 DI 配置阶段使用（无需解析服务）。</summary>
    public static TokenValidationParameters BuildValidationParameters(string jwtSecret)
        => new()
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = BuildSymmetricKey(jwtSecret),
            ClockSkew = TimeSpan.FromSeconds(30),
        };

    private static SymmetricSecurityKey BuildSymmetricKey(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            // 自动生成 32 个十六进制字符（2×Guid）——与 AGENTS.md 中的说明一致。
            secret = (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"))[..32];
        }
        return new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(secret));
    }

    private static string GenerateOtpSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        return Base32Encoding.ToString(bytes);
    }

    private static bool VerifyOtp(string secret, string code)
    {
        try
        {
            var totp = new Totp(Base32Encoding.ToBytes(secret));
            return totp.VerifyTotp(code, out _, new VerificationWindow(1, 1));
        }
        catch { return false; }
    }
}

internal static class RespExtensions
{
    public static bool IsSuccess<T>(this Resp<T> r) => r.Code == RespCodes.Ok;
}
