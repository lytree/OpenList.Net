using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 用于 /d/、/p/、/ad/、/ap/、/ae/ 等路由的 HMAC-SHA256 签名 URL 生成/校验器，
/// 并负责判定「这条路径到底要不要签名」。
/// <para>
/// 对应 Go 参考实现：
/// <list type="bullet">
/// <item><c>pkg/sign/hmac.go</c> —— 签名串格式
/// <c>base64url(hmac_sha256(path + ":" + expire)) + ":" + expire</c>（<c>base64.URLEncoding</c>，含 <c>=</c> 填充）。</item>
/// <item><c>internal/sign/sign.go</c> —— expire 取 <c>link_expiration</c> 设置（单位小时），0 表示永不过期。</item>
/// <item><c>server/common/sign.go</c> + <c>server/middlewares/down.go</c> ——
/// 是否签名只看 <c>sign_all</c> 与所属存储的 <c>enable_sign</c>；两处共用同一判定。</item>
/// </list>
/// </para>
/// <para>
/// 判定要素（设置与存储表）做 3 秒内存缓存：一次目录列举会对每个条目判定一次，
/// 不能每次都打数据库；3 秒足够让管理端的开关几乎立即生效。
/// </para>
/// </summary>
public sealed class SignService : ISignService
{
    /// <summary>全局强制签名开关（对应 Go 的 <c>conf.SignAll</c>）。</summary>
    private const string SignAllKey = "sign_all";

    /// <summary>链接有效期（小时，0 = 永不过期），对应 Go 的 <c>conf.LinkExpiration</c>。</summary>
    private const string LinkExpirationKey = "link_expiration";

    /// <summary>判定要素的缓存时长。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(3);

    private readonly byte[] _secret;
    private readonly IServiceScopeFactory? _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<SignService>? _logger;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private volatile Snapshot _snapshot = Snapshot.Empty;
    private DateTimeOffset _freshUntil = DateTimeOffset.MinValue;

    /// <summary>
    /// 创建签名服务实例，读取配置中的 JWT 密钥作为 HMAC 密钥。
    /// </summary>
    /// <param name="config">应用配置（含 JwtSecret）。</param>
    /// <param name="scopes">DI 作用域工厂（用于读取 <c>sign_all</c> / <c>link_expiration</c> 与存储表）。</param>
    /// <param name="time">时间源（可选，默认系统时钟）。</param>
    /// <param name="logger">日志记录器（可选）。</param>
    public SignService(
        AppConfiguration config,
        IServiceScopeFactory? scopes = null,
        TimeProvider? time = null,
        ILogger<SignService>? logger = null)
    {
        _secret = Encoding.UTF8.GetBytes(config.JwtSecret ?? string.Empty);
        _scopes = scopes;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>
    /// 解析某路径的签名策略，等价于 Go 的：
    /// <code>
    /// needSign(meta, path) = SignAll || IsStorageSignEnabled(path)
    /// </code>
    /// 我们的 meta 密码目录尚未在 fs/down 链路上接线，故此处只看前两项；
    /// 等密码门禁补齐后应在这里追加 <c>meta.Password != "" &amp;&amp; MetaCoversPath(...)</c>。
    /// </summary>
    public async Task<SignContext> GetContextAsync(string path, CancellationToken ct = default)
    {
        var snap = await GetSnapshotAsync(ct);
        var need = snap.SignAll || IsStorageSignEnabled(snap, path);
        if (!need) return SignContext.NotNeeded;

        var expireAt = snap.LinkExpirationHours > 0
            ? _time.GetUtcNow().AddHours(snap.LinkExpirationHours).ToUnixTimeSeconds()
            : 0L;
        return new SignContext(true, expireAt);
    }

    /// <summary>
    /// 生成签名。与 Go 完全一致：<c>base64.URLEncoding</c> 编码（保留 <c>=</c> 填充），
    /// 后接冒号与过期时间戳。
    /// </summary>
    public string Sign(string path, long expireAt = 0)
    {
        var expireStr = expireAt.ToString(CultureInfo.InvariantCulture);
        using var hmac = new HMACSHA256(_secret);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(path + ":" + expireStr));
        return Base64UrlEncode(hash) + ":" + expireStr;
    }

    /// <summary>
    /// 校验签名：先判过期，再常量时间比较重算结果。
    /// 比较时忽略 base64 的 <c>=</c> 填充差异——历史版本生成的是去填充签名，
    /// 而 Go 生成的是带填充签名，两者都应被接受。
    /// </summary>
    public bool Verify(string path, string sign)
    {
        if (string.IsNullOrEmpty(sign)) return false;
        // 对齐 Go middlewares.Down：strings.TrimSuffix(s, "/")。
        sign = sign.TrimEnd('/');

        var colon = sign.LastIndexOf(':');
        if (colon < 0) return false;
        if (!long.TryParse(sign[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expire))
            return false;
        // expire=0 表示永不过期；否则判断是否已失效。
        if (expire != 0 && expire < _time.GetUtcNow().ToUnixTimeSeconds()) return false;

        var expected = Sign(path, expire);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(StripPadding(expected)),
            Encoding.UTF8.GetBytes(StripPadding(sign)));
    }

    /// <summary>
    /// 丢弃判定要素缓存，使下一次判定重新读取设置与存储表。
    /// 管理端保存设置后调用可让开关立即生效；测试也用它避开缓存等待。
    /// </summary>
    public void InvalidateCache() => _freshUntil = DateTimeOffset.MinValue;

    /// <summary>去掉签名串中 base64 部分的 <c>=</c> 填充，仅用于比较。</summary>
    private static string StripPadding(string value)
    {
        var colon = value.LastIndexOf(':');
        return colon < 0 ? value.TrimEnd('=') : value[..colon].TrimEnd('=') + value[colon..];
    }

    /// <summary>是否属于「开启了签名访问」的存储：取最长匹配的启用存储的 <c>enable_sign</c>。</summary>
    private static bool IsStorageSignEnabled(Snapshot snapshot, string path)
    {
        if (snapshot.Storages.Count == 0) return false;
        var norm = NormalizePath(path);
        var bestLength = -1;
        var enabled = false;
        foreach (var (mountPath, enableSign) in snapshot.Storages)
        {
            if (mountPath.Length <= bestLength) continue;
            if (!IsMountPrefix(mountPath, norm)) continue;
            bestLength = mountPath.Length;
            enabled = enableSign;
        }
        return enabled;
    }

    /// <summary>挂载点是否是路径的前缀（按整段匹配：<c>/a</c> 覆盖 <c>/a/b</c>，不覆盖 <c>/ab</c>）。</summary>
    private static bool IsMountPrefix(string mountPath, string path)
        => mountPath == "/"
           || path.Equals(mountPath, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(mountPath + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>规范化路径：补前导 <c>/</c>，去掉末尾 <c>/</c>（根路径保持 <c>/</c>）。</summary>
    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        var p = path.Replace('\\', '/').Trim();
        if (!p.StartsWith('/')) p = "/" + p;
        if (p.Length > 1) p = p.TrimEnd('/');
        return p.Length == 0 ? "/" : p;
    }

    /// <summary>把存储的挂载路径规范成与 <see cref="NormalizePath"/> 一致的形式（末尾不带 <c>/</c>）。</summary>
    private static string NormalizeMount(string mountPath) => NormalizePath(mountPath);

    /// <summary>读取判定要素；命中缓存时直接返回，过期后单飞刷新。</summary>
    private async Task<Snapshot> GetSnapshotAsync(CancellationToken ct)
    {
        if (_scopes is null) return Snapshot.Empty;
        if (_time.GetUtcNow() < _freshUntil) return _snapshot;

        await _refreshGate.WaitAsync(ct);
        try
        {
            if (_time.GetUtcNow() < _freshUntil) return _snapshot;

            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetService<OpenListDbContext>();
            if (db is null) return _snapshot;

            var settings = await db.Settings.AsNoTracking()
                .Where(s => s.Key == SignAllKey || s.Key == LinkExpirationKey)
                .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
            var signAll = ParseBool(settings.GetValueOrDefault(SignAllKey));
            var hours = ParseInt(settings.GetValueOrDefault(LinkExpirationKey));

            var storages = await db.Storages.AsNoTracking()
                .Where(s => !s.Disabled)
                .Select(s => new { s.MountPath, s.EnableSign })
                .ToListAsync(ct);

            _snapshot = new Snapshot(
                signAll,
                hours,
                storages.Select(s => (NormalizeMount(s.MountPath), s.EnableSign)).ToList());
            _freshUntil = _time.GetUtcNow() + CacheTtl;
            return _snapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 数据库暂时不可用时不要放大成 500：退回「不签名」并短暂缓存，
            // 避免每个请求都重试。签名是附加保护，不该成为可用性单点。
            _logger?.LogWarning(ex, "读取签名策略失败，暂按不签名处理");
            _freshUntil = _time.GetUtcNow() + CacheTtl;
            return _snapshot;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static bool ParseBool(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");

    private static int ParseInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    /// <summary>URL 安全的 Base64（与 Go 的 <c>base64.URLEncoding</c> 一致，<b>保留</b>填充）。</summary>
    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');

    /// <summary>签名判定所需的全部要素快照。</summary>
    private sealed record Snapshot(
        bool SignAll,
        int LinkExpirationHours,
        IReadOnlyList<(string MountPath, bool EnableSign)> Storages)
    {
        public static readonly Snapshot Empty = new(false, 0, []);
    }
}
