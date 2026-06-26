namespace OpenList.Core.Configuration;

/// <summary>
/// Root application configuration bound from appsettings.json (OpenList__ prefix env vars override).
/// </summary>
public sealed class AppConfiguration
{
    public string JwtSecret { get; set; } = string.Empty;
    public int TokenExpiresIn { get; set; } = 48;
    public string AdminUsername { get; set; } = "admin";
    public string AdminPassword { get; set; } = "admin";
    public DatabaseConfig Database { get; set; } = new();
    public string DistDir { get; set; } = "../../frontend/dist";
    public string TempDir { get; set; } = "data/temp";
    public SchemeConfig Scheme { get; set; } = new();
    public CorsConfig Cors { get; set; } = new();
    public S3Config S3 { get; set; } = new();
    public FtpConfig FTP { get; set; } = new();
    public SftpConfig SFTP { get; set; } = new();
    public string SearchIndex { get; set; } = "database";
    public LdapConfig Ldap { get; set; } = new();
}

/// <summary>
/// LDAP server configuration for <c>/api/auth/login/ldap</c>.
/// All fields optional — empty <see cref="Url"/> disables LDAP entirely.
/// </summary>
public sealed class LdapConfig
{
    /// <summary>Server URL, e.g. <c>ldap://host:389</c> or <c>ldaps://host:636</c>. Empty = disabled.</summary>
    public string Url { get; set; } = string.Empty;
    /// <summary>Bind DN template with <c>%s</c> placeholder for the username (e.g. <c>uid=%s,ou=users,dc=example,dc=com</c>).</summary>
    public string BindDn { get; set; } = "uid=%s,ou=users,dc=example,dc=com";
    /// <summary>Base DN for user lookups when <see cref="BindDn"/> is not a template.</summary>
    public string UserBase { get; set; } = string.Empty;
    /// <summary>Attribute used as the username filter (default <c>uid</c>).</summary>
    public string UserFilter { get; set; } = "uid";
    /// <summary>Whether TLS is required for plain <c>ldap://</c> URLs (STARTTLS).</summary>
    public bool StartTls { get; set; }
    /// <summary>Skip certificate validation (useful for self-signed dev servers).</summary>
    public bool SkipVerify { get; set; } = true;
}

public sealed class DatabaseConfig
{
    public string Type { get; set; } = "sqlite";
    public string DBFile { get; set; } = "data/data.db";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 5432;
    public string User { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TablePrefix { get; set; } = "x_";
}

public sealed class SchemeConfig
{
    public string Address { get; set; } = "0.0.0.0";
    public int HttpPort { get; set; } = 5244;
    public int HttpsPort { get; set; } = 0;
    public string CertFile { get; set; } = string.Empty;
    public string KeyFile { get; set; } = string.Empty;
    public bool ForceHttps { get; set; } = false;
}

public sealed class CorsConfig
{
    public string[] AllowOrigins { get; set; } = ["*"];
    public string[] AllowMethods { get; set; } = ["*"];
    public string[] AllowHeaders { get; set; } = ["*"];
}

public sealed class S3Config
{
    public bool Enable { get; set; }
    public int Port { get; set; } = 5246;
}

public sealed class FtpConfig
{
    public bool Enable { get; set; }
    public string Listen { get; set; } = ":5221";
}

public sealed class SftpConfig
{
    public bool Enable { get; set; }
    public string Listen { get; set; } = ":5222";
}
