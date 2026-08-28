namespace OpenList.Core.Configuration;

/// <summary>
/// 应用程序根配置，由 appsettings.json 绑定（OpenList__ 前缀的环境变量可覆盖）。
/// </summary>
public sealed class AppConfiguration
{
    /// <summary>JWT 签名密钥。</summary>
    public string JwtSecret { get; set; } = string.Empty;
    /// <summary>令牌有效期（小时）。</summary>
    public int TokenExpiresIn { get; set; } = 48;
    /// <summary>默认管理员用户名。</summary>
    public string AdminUsername { get; set; } = "admin";
    /// <summary>默认管理员密码。</summary>
    public string AdminPassword { get; set; } = "admin";
    /// <summary>数据库配置。</summary>
    public DatabaseConfig Database { get; set; } = new();
    /// <summary>前端静态文件目录（SPA）。</summary>
    public string DistDir { get; set; } = "../../frontend/dist";
    /// <summary>临时目录。</summary>
    public string TempDir { get; set; } = "data/temp";
    /// <summary>监听方案（地址/端口）配置。</summary>
    public SchemeConfig Scheme { get; set; } = new();
    /// <summary>CORS 配置。</summary>
    public CorsConfig Cors { get; set; } = new();
    /// <summary>S3 网关配置。</summary>
    public S3Config S3 { get; set; } = new();
    /// <summary>FTP 服务器配置。</summary>
    public FtpConfig FTP { get; set; } = new();
    /// <summary>SFTP 服务器配置。</summary>
    public SftpConfig SFTP { get; set; } = new();
    /// <summary>搜索索引后端，默认 "database"。</summary>
    public string SearchIndex { get; set; } = "database";
    /// <summary>LDAP 配置。</summary>
    public LdapConfig Ldap { get; set; } = new();
    /// <summary>MCP 服务器配置。</summary>
    public MCPConfig MCP { get; set; } = new();
}

/// <summary>
/// MCP（Model Context Protocol）服务器配置。与 Go 的 <c>internal/conf.MCP</c> 对应。
/// 禁用时，<c>/mcp</c> 返回 403。
/// </summary>
public sealed class MCPConfig
{
    /// <summary>是否启用 MCP 服务器。</summary>
    public bool Enable { get; set; }
}

/// <summary>
/// LDAP 服务器配置，服务于 <c>/api/auth/login/ldap</c>。
/// 所有字段可选——为空的 <see cref="Url"/> 会彻底禁用 LDAP。
/// </summary>
public sealed class LdapConfig
{
    /// <summary>服务器 URL，如 <c>ldap://host:389</c> 或 <c>ldaps://host:636</c>。为空表示禁用。</summary>
    public string Url { get; set; } = string.Empty;
    /// <summary>绑定 DN 模板，用 <c>%s</c> 占位用户名（如 <c>uid=%s,ou=users,dc=example,dc=com</c>）。</summary>
    public string BindDn { get; set; } = "uid=%s,ou=users,dc=example,dc=com";
    /// <summary>当 <see cref="BindDn"/> 不是模板时，用于用户查询的基础 DN。</summary>
    public string UserBase { get; set; } = string.Empty;
    /// <summary>用作用户名过滤的属性（默认 <c>uid</c>）。</summary>
    public string UserFilter { get; set; } = "uid";
    /// <summary>明文 <c>ldap://</c> URL 是否需要 TLS（STARTTLS）。</summary>
    public bool StartTls { get; set; }
    /// <summary>是否跳过证书校验（对自签名的开发服务器有用）。</summary>
    public bool SkipVerify { get; set; } = true;
}

/// <summary>
/// 数据库连接配置。
/// </summary>
public sealed class DatabaseConfig
{
    /// <summary>数据库类型，默认 "sqlite"。</summary>
    public string Type { get; set; } = "sqlite";
    /// <summary>SQLite 数据库文件路径。</summary>
    public string DBFile { get; set; } = "data/data.db";
    /// <summary>数据库主机地址。</summary>
    public string Host { get; set; } = string.Empty;
    /// <summary>数据库端口。</summary>
    public int Port { get; set; } = 5432;
    /// <summary>数据库用户。</summary>
    public string User { get; set; } = string.Empty;
    /// <summary>数据库密码。</summary>
    public string Password { get; set; } = string.Empty;
    /// <summary>数据库名称。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>表名前缀。</summary>
    public string TablePrefix { get; set; } = "x_";
}

/// <summary>
/// 监听地址与端口配置。
/// </summary>
public sealed class SchemeConfig
{
    /// <summary>监听地址。</summary>
    public string Address { get; set; } = "0.0.0.0";
    /// <summary>HTTP 端口。</summary>
    public int HttpPort { get; set; } = 5244;
    /// <summary>HTTPS 端口（0 表示不启用）。</summary>
    public int HttpsPort { get; set; } = 0;
    /// <summary>证书文件路径。</summary>
    public string CertFile { get; set; } = string.Empty;
    /// <summary>私钥文件路径。</summary>
    public string KeyFile { get; set; } = string.Empty;
    /// <summary>是否强制使用 HTTPS。</summary>
    public bool ForceHttps { get; set; } = false;
}

/// <summary>
/// 跨域资源共享（CORS）配置。
/// </summary>
public sealed class CorsConfig
{
    /// <summary>允许的来源。</summary>
    public string[] AllowOrigins { get; set; } = ["*"];
    /// <summary>允许的请求方法。</summary>
    public string[] AllowMethods { get; set; } = ["*"];
    /// <summary>允许的请求头。</summary>
    public string[] AllowHeaders { get; set; } = ["*"];
}

/// <summary>
/// S3 网关配置。
/// </summary>
public sealed class S3Config
{
    /// <summary>是否启用。</summary>
    public bool Enable { get; set; }
    /// <summary>监听端口。</summary>
    public int Port { get; set; } = 5246;
}

/// <summary>
/// FTP 服务器配置。
/// </summary>
public sealed class FtpConfig
{
    /// <summary>是否启用。</summary>
    public bool Enable { get; set; }
    /// <summary>监听地址，默认 ":5221"。</summary>
    public string Listen { get; set; } = ":5221";
}

/// <summary>
/// SFTP 服务器配置。
/// </summary>
public sealed class SftpConfig
{
    /// <summary>是否启用。</summary>
    public bool Enable { get; set; }
    /// <summary>监听地址，默认 ":5222"。</summary>
    public string Listen { get; set; } = ":5222";
}
