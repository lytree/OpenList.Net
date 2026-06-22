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
