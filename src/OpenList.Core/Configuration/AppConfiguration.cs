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
    /// <summary>最大并发请求数，0 表示不限制（镜像 Go 的 conf.MaxConnections）。</summary>
    public int MaxConnections { get; set; }
    /// <summary>日志配置（镜像 Go 的 conf.Log；Serilog 请求日志中间件读取 Filter 过滤规则）。</summary>
    public LogConfig Log { get; set; } = new();
    /// <summary>LDAP 配置。</summary>
    public LdapConfig Ldap { get; set; } = new();
    /// <summary>MCP 服务器配置。</summary>
    public MCPConfig MCP { get; set; } = new();

    /// <summary>
    /// 启动期配置校验（对应 Go bootstrap 的配置检查）：不合法时抛出异常并中止启动。
    /// </summary>
    /// <exception cref="InvalidOperationException">任一关键配置非法时抛出，消息含具体原因。</exception>
    public void Validate()
    {
        if (!Database.Type.Equals("sqlite", StringComparison.OrdinalIgnoreCase) &&
            !Database.Type.Equals("postgres", StringComparison.OrdinalIgnoreCase) &&
            !Database.Type.Equals("postgresql", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unknown database type '{Database.Type}' (expected 'sqlite' or 'postgres').");

        if (Scheme.HttpPort is < -1 or > 65535)
            throw new InvalidOperationException($"Invalid HTTP port {Scheme.HttpPort} (expected -1..65535).");
        if (Scheme.HttpsPort is < 0 or > 65535)
            throw new InvalidOperationException($"Invalid HTTPS port {Scheme.HttpsPort} (expected 0..65535).");

        if (MaxConnections < 0)
            throw new InvalidOperationException($"Invalid MaxConnections {MaxConnections} (expected >= 0).");

        if (!SearchIndex.Equals("database", StringComparison.OrdinalIgnoreCase) &&
            !SearchIndex.Equals("bleve", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unknown search index backend '{SearchIndex}' (expected 'database' or 'bleve').");

        if (Log.MaxSize < 1 || Log.MaxBackups < 1 || Log.MaxAge < 1)
            throw new InvalidOperationException("Log.MaxSize/MaxBackups/MaxAge must be >= 1.");
    }
}

/// <summary>
/// 日志配置，镜像 Go 的 <c>internal/conf.LogConfig</c>：
/// 控制 Serilog 文件日志（大小/保留策略）与请求日志的过滤规则。
/// </summary>
public sealed class LogConfig
{
    /// <summary>是否启用文件日志（对应 Go 的 Log.Enable）。</summary>
    public bool Enable { get; set; } = true;
    /// <summary>日志文件路径。</summary>
    public string Name { get; set; } = "logs/openlist.log";
    /// <summary>单个日志文件最大大小（MB，对应 Go 的 MaxSize）。</summary>
    public int MaxSize { get; set; } = 50;
    /// <summary>保留的日志文件最大数量（对应 Go 的 MaxBackups）。</summary>
    public int MaxBackups { get; set; } = 30;
    /// <summary>日志文件最大保留天数（对应 Go 的 MaxAge）。</summary>
    public int MaxAge { get; set; } = 28;
    /// <summary>是否压缩旧日志文件（对应 Go 的 Compress）。</summary>
    public bool Compress { get; set; }
    /// <summary>请求日志过滤配置。</summary>
    public LogFilterConfig Filter { get; set; } = new();
}

/// <summary>
/// 请求日志过滤配置，镜像 Go 的 <c>LogFilterConfig</c>。
/// 启用后，命中任一过滤规则的请求将不被写入访问日志。
/// </summary>
public sealed class LogFilterConfig
{
    /// <summary>是否启用过滤。</summary>
    public bool Enable { get; set; }
    /// <summary>过滤规则列表（任一规则全部条件命中即跳过日志）。</summary>
    public List<LogFilterItem> Filters { get; set; } = [];
}

/// <summary>
/// 单条请求日志过滤规则，镜像 Go 的 <c>Filter</c>。规则内各条件为"与"关系，
/// 规则之间为"或"关系。
/// </summary>
public sealed class LogFilterItem
{
    /// <summary>客户端 IP 网段（CIDR，如 <c>127.0.0.0/8</c>）。为空表示不限制。</summary>
    public string CIDR { get; set; } = string.Empty;
    /// <summary>路径过滤：以 <c>/</c> 开头按前缀匹配，否则按包含匹配。为空表示不限制。</summary>
    public string Path { get; set; } = string.Empty;
    /// <summary>请求方法过滤（如 <c>HEAD</c>）。为空表示不限制。</summary>
    public string Method { get; set; } = string.Empty;
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
