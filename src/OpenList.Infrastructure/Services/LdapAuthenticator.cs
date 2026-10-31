using Microsoft.Extensions.DependencyInjection;
using Novell.Directory.Ldap;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// LDAP 认证器，基于 <c>Novell.Directory.Ldap.NETStandard</c>（跨平台）。
/// <para>
/// 配置来源：<b>设置表优先，appsettings 兜底</b>。管理台「LDAP 设置」组的
/// <c>ldap_server</c> / <c>ldap_manager_dn</c> / <c>ldap_manager_password</c> /
/// <c>ldap_user_search_base</c> / <c>ldap_user_search_filter</c> /
/// <c>ldap_skip_tls_verify</c> / <c>ldap_login_enabled</c> 是主配置，
/// 没填的项回退到 <see cref="AppConfiguration.Ldap"/>（含 <c>BindDn</c> 模板）。
/// 这样设置页保存后即时生效，无需重启；老部署只写 appsettings 也能继续工作
/// （启动时由 <c>DbInitializer</c> 一次性导入设置表）。
/// </para>
/// <para>
/// 支持两种绑定模式：
/// <list type="bullet">
/// <item>设置了 <c>ldap_user_search_base</c>（或 appsettings 的 <c>BindDn</c> 不是模板）时走搜索模式：
/// 先用服务账号（<c>ldap_manager_dn</c>）绑定，按过滤器搜索出用户 DN，再用用户密码绑定。</item>
/// <item><c>BindDn</c> 包含 <c>%s</c>（如 <c>uid=%s,ou=users,dc=example,dc=com</c>）且未配置搜索 base 时，
/// 直接模板替换得到 DN 后绑定。</item>
/// </list>
/// 对应 Go 参考实现 <c>internal/op/auth.go</c>。
/// </para>
/// </summary>
public sealed class LdapAuthenticator : ILdapAuthenticator
{
    /// <summary>appsettings 兜底配置（设置表缺项时使用）。</summary>
    private readonly LdapConfig _fallback;

    /// <summary>作用域工厂：每次认证时读一次设置表（LDAP 登录是低频操作，无需缓存）。</summary>
    private readonly IServiceScopeFactory _scopes;

    /// <summary>
    /// 创建 LDAP 认证器实例。
    /// </summary>
    /// <param name="config">应用配置，作为设置表的兜底默认值。</param>
    /// <param name="scopes">DI 作用域工厂（读取 scoped 的 <see cref="ISettingService"/>）。</param>
    public LdapAuthenticator(AppConfiguration config, IServiceScopeFactory scopes)
    {
        _fallback = config.Ldap ?? new();
        _scopes = scopes;
    }

    /// <inheritdoc />
    public async Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        var cfg = await ResolveAsync(ct);
        return cfg.LoginEnabled && !string.IsNullOrWhiteSpace(cfg.Server);
    }

    /// <summary>
    /// 校验指定用户名与密码是否能通过 LDAP 认证。
    /// </summary>
    /// <param name="username">待认证的用户名。</param>
    /// <param name="password">待认证的密码。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>认证成功返回 <c>true</c>；未启用、无效输入、凭证错误或目录异常均返回 <c>false</c>。</returns>
    public async Task<bool> AuthenticateAsync(string username, string password, CancellationToken ct = default)
    {
        // 登录页的开关与这里必须同源：关闭开关时即使目录可达也不接受认证。
        var cfg = await ResolveAsync(ct);
        if (!cfg.LoginEnabled || string.IsNullOrWhiteSpace(cfg.Server)) return false;
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return false;

        var (host, port, ssl) = ParseUrl(cfg.Server);
        using var conn = new LdapConnection();
        try
        {
            ct.ThrowIfCancellationRequested();
            if (ssl)
            {
                conn.SecureSocketLayer = true;
                if (cfg.SkipVerify)
                {
                    // 跳过自签名证书校验，便于连接开发环境。
                    conn.UserDefinedServerCertValidationDelegate += (_, _, _, _) => true;
                }
            }
            await conn.ConnectAsync(host, port, ct);

            // 起步 TLS（STARTTLS）很少与 LDAPS 搭建组合使用；需要 TLS 的用户应改用 ldaps://。
            // 4.x 已提供 StartTlsAsync，但为保持与既有配置语义一致，此处仍不做隐式升级。

            var dn = ResolveDn(cfg, username);
            if (dn is null)
            {
                // 搜索模式：先用服务账号（未配置则匿名）绑定以定位用户 DN。
                dn = await SearchUserDnAsync(conn, cfg, username, ct);
                if (dn is null) return false;
            }

            await conn.BindAsync(LdapConnection.LdapV3, dn, password, ct);
            return conn.Bound;
        }
        catch (LdapException)
        {
            // 凭证错误或连接被拒 —— 以认证失败返回，而非抛异常。
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { if (conn.Connected) conn.Disconnect(); } catch { /* 忽略 */ }
        }
    }

    /// <summary>
    /// 解析本次认证使用的运行配置：设置表优先，空值回退 appsettings。
    /// 读取失败（未建库、迁移中）时整体回退 appsettings，不抛异常。
    /// </summary>
    private async Task<RuntimeConfig> ResolveAsync(CancellationToken ct)
    {
        Dictionary<string, string> dict;
        try
        {
            using var scope = _scopes.CreateScope();
            var settings = scope.ServiceProvider.GetService<ISettingService>();
            if (settings is null) return RuntimeConfig.FromFallback(_fallback);
            var resp = await settings.ListAsync(ct).ConfigureAwait(false);
            if (resp.Code != 200 || resp.Data is null) return RuntimeConfig.FromFallback(_fallback);
            dict = resp.Data.ToDictionary(s => s.Key, s => s.Value ?? string.Empty, StringComparer.Ordinal);
        }
        catch
        {
            return RuntimeConfig.FromFallback(_fallback);
        }
        return RuntimeConfig.FromSettings(dict, _fallback);
    }

    /// <summary>
    /// 若 <see cref="RuntimeConfig.BindDn"/> 包含 <c>%s</c>，则用用户名替换占位符得到 DN；
    /// 否则返回 null，表示进入搜索模式。
    /// </summary>
    /// <param name="cfg">本次认证的运行配置。</param>
    /// <param name="username">要替换进 DN 模板的用户名。</param>
    /// <returns>模板化 DN，或 null（表示需要搜索用户 DN）。</returns>
    private static string? ResolveDn(RuntimeConfig cfg, string username)
    {
        var tpl = cfg.BindDn;
        if (!string.IsNullOrEmpty(tpl) && tpl.Contains("%s")) return tpl.Replace("%s", EscapeLdapDN(username));
        return null;
    }

    /// <summary>
    /// 用服务账号（未配置则匿名）绑定后，在 <c>UserBase</c> 下按用户过滤器搜索用户 DN。
    /// </summary>
    /// <param name="conn">已连接的 LDAP 连接。</param>
    /// <param name="cfg">本次认证的运行配置。</param>
    /// <param name="username">要定位的用户名。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>匹配的首个用户 DN，未找到返回 null。</returns>
    private static async Task<string?> SearchUserDnAsync(
        LdapConnection conn, RuntimeConfig cfg, string username, CancellationToken ct)
    {
        // 服务账号绑定：目录通常禁止匿名搜索；未配置 manager 时退回匿名并接受失败。
        try
        {
            if (!string.IsNullOrEmpty(cfg.ManagerDn))
                await conn.BindAsync(LdapConnection.LdapV3, cfg.ManagerDn, cfg.ManagerPassword, ct);
            else
                await conn.BindAsync(LdapConnection.LdapV3, "", "", ct);
        }
        catch { /* 匿名/服务账号绑定被拒，交由下面的 Bound 判定 */ }
        if (!conn.Bound) return null;

        var filter = cfg.BuildFilter(username);
        var attrs = new[] { cfg.UserFilterAttribute };
        var results = await conn.SearchAsync(cfg.UserBase, LdapConnection.ScopeSub, filter, attrs, false, ct);
        // 取首个匹配条目的 DN。
        while (await results.HasMoreAsync(ct))
        {
            try { return (await results.NextAsync(ct)).Dn; }
            catch (LdapException) { /* 跳过异常条目 */ }
        }
        return null;
    }

    /// <summary>
    /// 解析 LDAP URL 为主机、端口与是否使用 SSL。
    /// </summary>
    /// <param name="url">形如 <c>ldap://host:port</c> 或 <c>ldaps://host:port</c> 的地址。</param>
    /// <returns>主机、端口与 SSL 标志组成的元组。</returns>
    private static (string Host, int Port, bool Ssl) ParseUrl(string url)
    {
        var u = url.Trim();
        var ssl = u.StartsWith("ldaps://", StringComparison.OrdinalIgnoreCase);
        var scheme = ssl ? "ldaps://" : "ldap://";
        if (u.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) u = u[scheme.Length..];
        // 去掉尾部多余路径。
        var slash = u.IndexOf('/');
        if (slash >= 0) u = u[..slash];
        var colon = u.IndexOf(':');
        if (colon >= 0 && int.TryParse(u[(colon + 1)..], out var p))
            return (u[..colon], p, ssl);
        return (u, ssl ? 636 : 389, ssl);
    }

    // RFC 4514 DN 转义：替换替换进 DN 时必须转义的特殊字符。
    private static string EscapeLdapDN(string s)
        => s.Replace("\\", "\\\\").Replace(",", "\\,").Replace("+", "\\+")
            .Replace("\"", "\\\"").Replace("<", "\\<").Replace(">", "\\>")
            .Replace(";", "\\;").Replace("=", "\\=");

    // RFC 4515 过滤器转义：括号、星号、反斜杠与 NUL。
    private static string EscapeLdapFilter(string s)
        => s.Replace("\\", "\\5c").Replace("(", "\\28").Replace(")", "\\29")
            .Replace("*", "\\2a").Replace("\0", "\\00");

    /// <summary>
    /// 一次认证所需的运行配置（设置表映射 + appsettings 兜底）。
    /// </summary>
    private sealed record RuntimeConfig
    {
        /// <summary>服务器地址（<c>ldap_server</c> / <c>Ldap:Url</c>）。</summary>
        public string Server { get; init; } = string.Empty;

        /// <summary>DN 模板（仅来自 appsettings 的 <c>BindDn</c>，且仅在未配置搜索 base 时生效）。</summary>
        public string BindDn { get; init; } = string.Empty;

        /// <summary>用户搜索基础 DN。</summary>
        public string UserBase { get; init; } = string.Empty;

        /// <summary>用户过滤配置：可以是属性名（<c>uid</c>）或带 <c>%s</c> 的过滤器模板（<c>(uid=%s)</c>）。</summary>
        public string UserFilter { get; init; } = "uid";

        /// <summary>服务账号 DN（用于搜索绑定）。</summary>
        public string ManagerDn { get; init; } = string.Empty;

        /// <summary>服务账号密码。</summary>
        public string ManagerPassword { get; init; } = string.Empty;

        /// <summary>是否跳过证书校验。</summary>
        public bool SkipVerify { get; init; } = true;

        /// <summary><c>ldap_login_enabled</c> 开关。</summary>
        public bool LoginEnabled { get; init; }

        /// <summary>搜索时作为返回属性的用户名属性名。</summary>
        public string UserFilterAttribute => UserFilter.Contains('%') || UserFilter.Contains('(') ? "uid" : UserFilter;

        /// <summary>构造 RFC 4515 过滤器：模板形式直接用 <c>%s</c> 替换，属性名形式包成 <c>(attr=value)</c>。</summary>
        public string BuildFilter(string username)
        {
            var escaped = EscapeLdapFilter(username);
            return UserFilter.Contains('%') || UserFilter.Contains('(')
                ? UserFilter.Replace("%s", escaped)
                : $"({UserFilter}={escaped})";
        }

        /// <summary>只用 appsettings 构造（设置表不可读时的兜底）。</summary>
        public static RuntimeConfig FromFallback(LdapConfig cfg) => new()
        {
            Server = cfg.Url ?? string.Empty,
            // 模板模式只在没有搜索 base 时启用：有了 base 就应该走「搜索 DN」而不是硬编码 DN。
            BindDn = string.IsNullOrWhiteSpace(cfg.UserBase) ? cfg.BindDn ?? string.Empty : string.Empty,
            UserBase = cfg.UserBase ?? string.Empty,
            UserFilter = string.IsNullOrWhiteSpace(cfg.UserFilter) ? "uid" : cfg.UserFilter,
            SkipVerify = cfg.SkipVerify,
            // appsettings 里配了 Url 即视为启用（旧语义），没有 Url 则关闭。
            LoginEnabled = !string.IsNullOrWhiteSpace(cfg.Url),
        };

        /// <summary>设置表优先、appsettings 兜底的映射。</summary>
        public static RuntimeConfig FromSettings(IReadOnlyDictionary<string, string> settings, LdapConfig fallback)
        {
            var base0 = FromFallback(fallback);

            string Pick(string key, string fallbackValue)
                => settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : fallbackValue;

            bool PickBool(string key, bool fallbackValue)
                => settings.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallbackValue;

            var searchBase = Pick("ldap_user_search_base", base0.UserBase);
            return new RuntimeConfig
            {
                Server = Pick("ldap_server", base0.Server),
                // 配置了搜索 base 就不再使用 DN 模板（否则会绕过搜索直接硬编码 DN）。
                BindDn = string.IsNullOrWhiteSpace(searchBase) ? base0.BindDn : string.Empty,
                UserBase = searchBase,
                UserFilter = Pick("ldap_user_search_filter", base0.UserFilter),
                ManagerDn = Pick("ldap_manager_dn", string.Empty),
                ManagerPassword = Pick("ldap_manager_password", string.Empty),
                SkipVerify = PickBool("ldap_skip_tls_verify", base0.SkipVerify),
                // 设置表里该键始终存在（种子默认 false）；缺失时才退回「配置了 Url 即启用」。
                LoginEnabled = PickBool("ldap_login_enabled", base0.LoginEnabled),
            };
        }
    }
}
