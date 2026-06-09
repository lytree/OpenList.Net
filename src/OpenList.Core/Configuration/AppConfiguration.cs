namespace OpenList.Core.Configuration;

public class AppConfiguration
{
    public bool Force { get; set; }
    public string SiteURL { get; set; } = "";
    public string Cdn { get; set; } = "";
    public string JwtSecret { get; set; } = "";
    public int TokenExpiresIn { get; set; } = 48;
    public DatabaseConfig Database { get; set; } = new();
    public MeilisearchConfig Meilisearch { get; set; } = new();
    public SchemeConfig Scheme { get; set; } = new();
    public string TempDir { get; set; } = "data/temp";
    public string BleveDir { get; set; } = "data/bleve";
    public string? DistDir { get; set; }
    public LogConfig Log { get; set; } = new();
    public int DelayedStart { get; set; }
    public int AutoMemoryLimit { get; set; } = 4;
    public int MinFreeMemory { get; set; }
    public int MaxBlockLimit { get; set; }
    public int MaxConnections { get; set; }
    public int MaxConcurrency { get; set; } = 64;
    public bool TlsInsecureSkipVerify { get; set; }
    public TasksConfig Tasks { get; set; } = new();
    public CorsConfig Cors { get; set; } = new();
    public S3Config S3 { get; set; } = new();
    public FtpConfig FTP { get; set; } = new();
    public SftpConfig SFTP { get; set; } = new();
    public string LastLaunchedVersion { get; set; } = "";
    public string? ProxyAddress { get; set; }
    public string AdminUsername { get; set; } = "admin";
    public string AdminPassword { get; set; } = "admin";
}

public class DatabaseConfig
{
    public string Type { get; set; } = "sqlite";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string Name { get; set; } = "openlist";
    public string DBFile { get; set; } = "data/data.db";
    public string TablePrefix { get; set; } = "x_";
    public string SSLMode { get; set; } = "Prefer";
}

public class MeilisearchConfig
{
    public string Host { get; set; } = "http://localhost:7700";
    public string APIKey { get; set; } = "";
    public string Index { get; set; } = "openlist";
}

public class SchemeConfig
{
    public string Address { get; set; } = "0.0.0.0";
    public int HttpPort { get; set; } = 5244;
    public int HttpsPort { get; set; } = -1;
    public bool ForceHttps { get; set; }
    public string CertFile { get; set; } = "";
    public string CertPassword { get; set; } = "";
    public string KeyFile { get; set; } = "";
    public string UnixFile { get; set; } = "";
    public string UnixFilePerm { get; set; } = "";
    public bool EnableH2c { get; set; }
    public bool EnableH3 { get; set; }
}

public class LogConfig
{
    public bool Enable { get; set; } = true;
    public string Name { get; set; } = "data/log/log.log";
    public int MaxSize { get; set; } = 50;
    public int MaxBackups { get; set; } = 30;
    public int MaxAge { get; set; } = 28;
    public bool Compress { get; set; }
    public LogFilterConfig Filter { get; set; } = new();
}

public class LogFilterConfig
{
    public bool Enable { get; set; }
    public List<LogFilter> Filters { get; set; } = [];
}

public class LogFilter
{
    public string? CIDR { get; set; }
    public string? Path { get; set; }
    public string? Method { get; set; }
}

public class TaskConfig
{
    public int Workers { get; set; } = 5;
    public int MaxRetry { get; set; }
    public bool TaskPersistent { get; set; }
}

public class TasksConfig
{
    public TaskConfig Download { get; set; } = new() { Workers = 5, MaxRetry = 1 };
    public TaskConfig Transfer { get; set; } = new() { Workers = 5, MaxRetry = 2 };
    public TaskConfig Upload { get; set; } = new() { Workers = 5 };
    public TaskConfig Copy { get; set; } = new() { Workers = 5, MaxRetry = 2 };
    public TaskConfig Move { get; set; } = new() { Workers = 5, MaxRetry = 2 };
    public TaskConfig Decompress { get; set; } = new() { Workers = 5, MaxRetry = 2 };
    public TaskConfig DecompressUpload { get; set; } = new() { Workers = 5, MaxRetry = 2 };
    public bool AllowRetryCanceled { get; set; }
}

public class CorsConfig
{
    public List<string> AllowOrigins { get; set; } = ["*"];
    public List<string> AllowMethods { get; set; } = ["*"];
    public List<string> AllowHeaders { get; set; } = ["*"];
}

public class S3Config
{
    public bool Enable { get; set; }
    public int Port { get; set; } = 5246;
    public bool SSL { get; set; }
}

public class FtpConfig
{
    public bool Enable { get; set; }
    public string Listen { get; set; } = ":5221";
    public int FindPasvPortAttempts { get; set; } = 50;
    public bool ActiveTransferPortNon20 { get; set; }
    public int IdleTimeout { get; set; } = 900;
    public int ConnectionTimeout { get; set; } = 30;
    public bool DisableActiveMode { get; set; }
    public bool DefaultTransferBinary { get; set; }
    public bool EnableActiveConnIPCheck { get; set; } = true;
    public bool EnablePasvConnIPCheck { get; set; } = true;
}

public class SftpConfig
{
    public bool Enable { get; set; }
    public string Listen { get; set; } = ":5222";
}
