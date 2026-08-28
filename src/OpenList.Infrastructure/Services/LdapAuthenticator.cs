using Novell.Directory.Ldap;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// LDAP 认证器，基于 <c>Novell.Directory.Ldap.NETStandard</c>（跨平台）。
/// 支持两种绑定模式：
/// <list type="bullet">
/// <item><c>BindDn</c> 包含 <c>%s</c> — 使用模板化 DN 直接绑定（无需搜索）。</item>
/// <item><c>BindDn</c> 无占位符 — 先在 <c>UserBase</c> 下搜索用户 DN 再绑定。</item>
/// </list>
/// 底层 Novell 客户端是同步的；这里将其卸载到线程池执行，避免阻塞 ASP.NET 请求线程。
/// 对应 Go 参考实现 <c>internal/op/auth.go</c>。
/// </summary>
public sealed class LdapAuthenticator : ILdapAuthenticator
{
    private readonly LdapConfig _cfg;

    /// <summary>
    /// 创建 LDAP 认证器实例。
    /// </summary>
    /// <param name="config">应用配置，取其中 <c>Ldap</c> 段（无则使用空配置）。</param>
    public LdapAuthenticator(AppConfiguration config) => _cfg = config.Ldap ?? new();

    /// <summary>
    /// 指示 LDAP 是否已启用（配置了 Url 即为启用）。
    /// </summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(_cfg.Url);

    /// <summary>
    /// 校验指定用户名与密码是否能通过 LDAP 认证。
    /// </summary>
    /// <param name="username">待认证的用户名。</param>
    /// <param name="password">待认证的密码。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>认证成功返回 <c>true</c>；未启用、无效输入、凭证错误或目录异常均返回 <c>false</c>。</returns>
    public Task<bool> AuthenticateAsync(string username, string password, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (!Enabled) return false;
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return false;

            var (host, port, ssl) = ParseUrl(_cfg.Url);
            using var conn = new LdapConnection();
            try
            {
                ct.ThrowIfCancellationRequested();
                if (ssl)
                {
                    conn.SecureSocketLayer = true;
                    if (_cfg.SkipVerify)
                    {
                        // 跳过自签名证书校验，便于连接开发环境。
                        conn.UserDefinedServerCertValidationDelegate += (_, _, _, _) => true;
                    }
                }
                conn.Connect(host, port);

                // 起步 TLS（STARTTLS）很少与 LDAPS 搭建组合使用；需要 TLS 的用户应改用 ldaps://。
                // Novell 3.6 各版本的 STARTTLS 入口名称不一致，为保持可移植性这里跳过。

                var dn = ResolveDn(username);
                if (dn is null)
                {
                    // 搜索模式：先以匿名（或服务账号）绑定以定位用户 DN。
                    dn = SearchUserDn(conn, username);
                    if (dn is null) return false;
                }

                conn.Bind(LdapConnection.LdapV3, dn, password);
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
        }, ct);

    /// <summary>
    /// 若 <see cref="LdapConfig.BindDn"/> 包含 <c>%s</c>，则用用户名替换占位符得到 DN；
    /// 否则返回 null，表示进入搜索模式。
    /// </summary>
    /// <param name="username">要替换进 DN 模板的用户名。</param>
    /// <returns>模板化 DN，或 null（表示需要搜索用户 DN）。</returns>
    private string? ResolveDn(string username)
    {
        var tpl = _cfg.BindDn ?? "";
        if (tpl.Contains("%s")) return tpl.Replace("%s", EscapeLdapDN(username));
        return null;
    }

    /// <summary>
    /// 在 <c>UserBase</c> 下按用户过滤器搜索用户 DN。
    /// </summary>
    /// <param name="conn">已连接的 LDAP 连接。</param>
    /// <param name="username">要定位的用户名。</param>
    /// <returns>匹配的首个用户 DN，未找到返回 null。</returns>
    private string? SearchUserDn(LdapConnection conn, string username)
    {
        // 先尝试匿名绑定；若目录禁止匿名，搜索会直接抛异常并返回 null（不泄露有效用户名）。
        try { conn.Bind(LdapConnection.LdapV3, "", ""); } catch { /* 匿名可能被拒绝 */ }
        if (!conn.Bound) return null;

        var filter = $"({_cfg.UserFilter}={EscapeLdapFilter(username)})";
        var results = conn.Search(
            _cfg.UserBase,
            LdapConnection.ScopeSub,
            filter,
            new[] { _cfg.UserFilter },
            typesOnly: false);
        // 取首个匹配条目的 DN。
        while (results.HasMore())
        {
            try { return results.Next().Dn; }
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
}