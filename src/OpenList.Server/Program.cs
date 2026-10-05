using System.Text.Json;
using FubarDev.FtpServer;
using FubarDev.FtpServer.AccountManagement;
using FubarDev.FtpServer.FileSystem;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using OpenList.Core.Configuration;
using OpenList.Core.Drivers;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Services;
using OpenList.Server.Endpoints;
using OpenList.Server.Hosting;
using OpenList.Server.Middleware;
using OpenList.Server.Services;
using Scalar.AspNetCore;
using Serilog;

// =====================================================================
// OpenList.Server 程序入口（ASP.NET Core Minimal Hosting）
// ---------------------------------------------------------------------
// 这是一个"基于项目"的 ASP.NET Core 应用，使用顶层语句(Program.cs)完成：
//   1. 配置加载（OpenList__ 前缀的环境变量覆盖 appsettings.json）；
//   2. 依赖注入注册（数据库 EF Core / MountService / 各业务服务 /
//      JWT 鉴权 / CORS / FTP·SFTP 宿主服务 / Swagger·Scalar / Serilog）；
//   3. 中间件管道搭建（异常处理 → 请求日志 → 静态文件 → CORS → 认证/授权
//      → 限流 → 各 API 路由 → SPA 回退）；
//   4. 数据库初始化、种子管理员与加载全部存储后启动服务。
// 路由均以 Extension 方式写在 Endpoints/ 目录的各个静态类中（见 MapXxxApi）。
// =====================================================================
// Bootstrap logger — captures startup failures before the host is built.
// WebApplicationFactory<Program> 在测试进程里会多次调用顶层语句，先冻结 Log.Logger 后再次 CreateBootstrapLogger 会抛；
// 用 try/catch 兜底，复用已存在的 logger 即可。
try { Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger(); }
catch (InvalidOperationException) { /* Serilog 已冻结（测试 fixture 多次实例化），复用现有 logger。 */ }
try
{

var builder = WebApplication.CreateBuilder(args);

// Add environment variables with OpenList__ prefix (overrides appsettings.json)
builder.Configuration.AddEnvironmentVariables("OpenList__");

// Bind configuration (OpenList__ prefix env vars override)
builder.Services.Configure<AppConfiguration>(builder.Configuration);
var appConfig = builder.Configuration.Get<AppConfiguration>() ?? new AppConfiguration();

// Phase 16: fail fast on invalid critical config (mirrors Go bootstrap checks).
appConfig.Validate();

// Phase 16: Serilog — console sink always on; file sink (with Go-style
// rotation settings MaxSize/MaxBackups/MaxAge) when Log.Enable. ILogger<T>
// used across the app (incl. RequestLogMiddleware) flows to both sinks.
builder.Host.UseSerilog((_, _, lc) =>
{
    // 框架噪音（EF 的 SQL 逐语句日志等）压到 Warning，应用自身日志不受影响
    lc.MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning);
    lc.MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning);
    lc.WriteTo.Console();
    if (appConfig.Log.Enable && !string.IsNullOrWhiteSpace(appConfig.Log.Name))
    {
        var logPath = Path.GetFullPath(appConfig.Log.Name);
        var logDir = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(logDir)) Directory.CreateDirectory(logDir);
        lc.WriteTo.File(logPath,
            rollingInterval: RollingInterval.Day,
            fileSizeLimitBytes: appConfig.Log.MaxSize * 1024L * 1024L,
            rollOnFileSizeLimit: true,
            retainedFileCountLimit: appConfig.Log.MaxBackups,
            retainedFileTimeLimit: TimeSpan.FromDays(appConfig.Log.MaxAge));
    }
});

// JSON: camelCase, case-insensitive
builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

// Time provider (testable)
builder.Services.AddSingleton(TimeProvider.System);

// Database
builder.Services.AddSingleton(appConfig);
builder.Services.AddSingleton(appConfig.Database);
builder.Services.AddDbContext<OpenListDbContext>(opts =>
{
    if (appConfig.Database.Type.Equals("postgres", StringComparison.OrdinalIgnoreCase) ||
        appConfig.Database.Type.Equals("postgresql", StringComparison.OrdinalIgnoreCase))
    {
        // Npgsql: build connection string from DatabaseConfig fields.
        var connStr = new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = appConfig.Database.Host,
            Port = appConfig.Database.Port,
            Username = appConfig.Database.User,
            Password = appConfig.Database.Password,
            Database = appConfig.Database.Name,
        }.ToString();
        // Each provider has its own migration assembly (EF Core 10 dropped
        // MigrationsNamespace; separate assemblies are the official multi-
        // provider approach). Column types differ: BOOLEAN vs INTEGER,
        // TIMESTAMPTZ vs TEXT, BIGSERIAL vs INTEGER AUTOINCREMENT.
        opts.UseNpgsql(connStr, npgsql => npgsql
            .MigrationsAssembly("OpenList.Migrations.Postgres"));
    }
    else
    {
        opts.UseSqlite($"Data Source={appConfig.Database.DBFile}", sqlite => sqlite
            .MigrationsAssembly("OpenList.Migrations.Sqlite"));
    }
});

// Core services
// DbContext-consuming services are Scoped to match the EF Core DbContext lifetime.
// IStorageAdditionSink lets storage drivers persist runtime config changes (notably refreshed
// OAuth tokens) back to x_storages. It's a singleton like MountService and creates its own
// DI scope per write to reach the scoped DbContext. Registered BEFORE MountService so the
// optional constructor parameter resolves.
builder.Services.AddSingleton<IStorageAdditionSink, StorageAdditionSink>();
builder.Services.AddSingleton<MountService>();
builder.Services.AddScoped<IUserService, UserService>();
// ILdapAuthenticator is a singleton because it only depends on AppConfiguration.
// JwtService picks it up optionally — empty Url means Enabled=false and /login/ldap returns 501.
builder.Services.AddSingleton<ILdapAuthenticator, LdapAuthenticator>();
builder.Services.AddScoped<IAuthService, JwtService>();
// ISsoService depends on IUserService + IAuthService (both scoped) so it's scoped too.
// It reads SSO config from the settings table at request time — admins can toggle
// sso_login_enabled / sso_client_id etc. from the frontend without a restart.
builder.Services.AddHttpClient("sso");
builder.Services.AddScoped<ISsoService, SsoService>();
builder.Services.AddScoped<IStorageService, StorageService>();
builder.Services.AddScoped<ISettingService, SettingService>();
builder.Services.AddScoped<IMetaService, MetaService>();
builder.Services.AddScoped<IMessageService, MessageService>();
builder.Services.AddSingleton<IBroadcastService, BroadcastService>();
builder.Services.AddScoped<ISharingService, SharingService>();
builder.Services.AddScoped<ISshKeyService, SshKeyService>();
// IHttpContextAccessor lets WebAuthnService read the browser origin (scheme + host)
// to build the FIDO2 Relying Party config per request.
builder.Services.AddHttpContextAccessor();
// IWebAuthnService depends on OpenListDbContext + IAuthService + ISettingService
// (all scoped) + IHttpContextAccessor (singleton) so it's scoped too.
builder.Services.AddScoped<IWebAuthnService, WebAuthnService>();
builder.Services.AddSingleton<IScanService, ScanService>();
builder.Services.AddSingleton<ISearchService, SearchService>();
builder.Services.AddSingleton<ITaskService, TaskService>();
// SignService must be singleton (matches MountService lifetime) so FileSystemService
// can resolve it. Uses the same JWT secret for HMAC signing.
builder.Services.AddSingleton<ISignService, SignService>();
// Offline-download tool adapters (aria2 / qBittorrent / Transmission) talk to external
// daemons over HTTP — register a named HttpClient per tool so timeouts/policies can be
// tuned independently. IHttpClientFactory is the standard ASP.NET Core pattern.
builder.Services.AddHttpClient("offline_download_aria2");
builder.Services.AddHttpClient("offline_download_qbit");
builder.Services.AddHttpClient("offline_download_transmission");
// Provide a lazy IFileSystemService factory for OfflineDownloadService. This breaks
// the construction-time cycle between IFileSystemService (resolves IOfflineDownloadService
// in its singleton factory below) and IOfflineDownloadService (depends on IFileSystemService).
// The Func only captures sp, so building OfflineDownloadService never resolves IFileSystemService.
builder.Services.AddSingleton<Func<IFileSystemService>>(sp => () => sp.GetRequiredService<IFileSystemService>());
// IOfflineDownloadService is a singleton because it only depends on singletons
// (ITaskService, IHttpClientFactory) + IServiceScopeFactory for the scoped ISettingService lookups,
// plus the lazy Func<IFileSystemService>. Resolving it never triggers IFileSystemService construction.
builder.Services.AddSingleton<IOfflineDownloadService, OfflineDownloadService>();
// ITorrentService is a singleton — torrent parsing is stateless and only needs the
// mount registry to resolve the destination storage for rapid_upload. Adding new
// drivers (e.g. Cloud189PC) would plug into its capability check here.
builder.Services.AddSingleton<ITorrentService, TorrentService>();
// ObjsUpdateHooks is a singleton publish/subscribe registry that decouples
// IFileSystemService (publisher) from ISearchService (subscriber) so incremental
// search-index updates don't create a construction-time dependency cycle.
builder.Services.AddSingleton<IObjsUpdateHooks, ObjsUpdateHooks>();
// Rate limiters — singleton manager holding the 4 byte-token-bucket limiters
// (client/server × download/upload), refreshed from the settings table.
builder.Services.AddSingleton<RateLimitManager>();
// FileSystemService is a singleton; the 4-arg constructor wires in ISignService,
// IOfflineDownloadService (both singletons) and the IObjsUpdateHooks registry so
// AddOfflineDownloadAsync and incremental index publishing can delegate.
// IServiceScopeFactory is used to resolve the scoped IMetaService per request
// (for nearest-meta lookup in /fs/list and /fs/get); ITaskService enables the
// task-based batch move/copy matching Go's FsMove/FsCopy semantics.
builder.Services.AddSingleton<IFileSystemService>(sp =>
    ActivatorUtilities.CreateInstance<FileSystemService>(sp,
        sp.GetRequiredService<MountService>(),
        sp.GetService<ISignService>(),
        sp.GetService<IOfflineDownloadService>(),
        sp.GetService<IObjsUpdateHooks>(),
        sp.GetService<IServiceScopeFactory>(),
        sp.GetService<ITaskService>()));
// Multipart (chunked) upload sessions — singleton; depends only on IFileSystemService.
builder.Services.AddSingleton<IMultipartUploadService, MultipartUploadService>();
// S3 gateway multipart-upload sessions — singleton; parts staged on disk, TTL-reaped
// (mirrors Go server/s3/multipart.go). TTL comes from S3.MultipartTTL.
builder.Services.AddSingleton(sp =>
{
    var store = ActivatorUtilities.CreateInstance<S3MultipartStore>(sp);
    store.Ttl = S3MultipartStore.ParseTtl(appConfig.S3.MultipartTTL);
    return store;
});
// MCP server (Streamable HTTP) — singleton; depends on IFileSystemService + AppConfiguration.
builder.Services.AddSingleton<IMcpServerService, McpServerService>();
// WebDAV in-memory lock store — singleton so LOCK/UNLOCK state persists across
// requests in the same process (mirrors Go's webdav.NewMemLS()).
builder.Services.AddSingleton<IWebDavLockStore, InMemoryWebDavLockStore>();

// JWT auth — frontend sends token WITHOUT "Bearer " prefix.
// Auto-generate a stable secret once and persist it next to the database file so
// tokens survive server restarts. If JwtSecret is set explicitly in config, that takes precedence.
if (string.IsNullOrWhiteSpace(appConfig.JwtSecret))
{
    var dataDir = Path.GetDirectoryName(Path.GetFullPath(appConfig.Database.DBFile)) ?? AppContext.BaseDirectory;
    var secretFile = Path.Combine(dataDir, "jwt_secret");
    if (File.Exists(secretFile))
        appConfig.JwtSecret = File.ReadAllText(secretFile).Trim();
    if (string.IsNullOrWhiteSpace(appConfig.JwtSecret))
    {
        appConfig.JwtSecret = (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"))[..32];
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(secretFile, appConfig.JwtSecret);
    }
}
var jwtSecret = appConfig.JwtSecret;
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = JwtService.BuildValidationParameters(jwtSecret);
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                // Accept raw token (no "Bearer " prefix) from Authorization header.
                var token = ctx.Request.Headers.Authorization.ToString();
                if (!string.IsNullOrEmpty(token))
                {
                    ctx.Token = token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        ? token[7..]
                        : token;
                }
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("admin", p => p.RequireRole(((int)UserRole.Admin).ToString()));

// CORS
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()
    .WithExposedHeaders("Content-Range", "Accept-Ranges", "Content-Length")));

// Kestrel: 10 GB body limit for WebDAV uploads
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 10L * 1024 * 1024 * 1024);

// Phase 12: FTP/SFTP hosted servers (disabled by default via FTP:Enable / SFTP:Enable)
builder.Services.AddFtpServer(s => s
    .Services
    .AddScoped<FubarDev.FtpServer.AccountManagement.IMembershipProvider, OpenListFtpMembershipProvider>()
    .AddSingleton<FubarDev.FtpServer.FileSystem.IFileSystemClassFactory, MountFtpFileSystemFactory>());
builder.Services.Configure<FubarDev.FtpServer.FtpServerOptions>(opts =>
{
    var (addr, port) = FtpListenParser.Parse(appConfig.FTP.Listen);
    opts.ServerAddress = addr;
    opts.Port = port;
});
builder.Services.AddHostedService<FtpHostedService>();
builder.Services.AddHostedService<SftpHostedService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Phase 16: global error handling (outermost — swallows exceptions into a
// Resp JSON, mirrors gin.Recovery) then request access log (mirrors
// FilteredLogger). Both wrap static files + API + SPA fallback.
app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseMiddleware<RequestLogMiddleware>(appConfig.Log.Filter);

// Serve static files BEFORE routing so that asset requests (e.g. /assets/*.js)
// are handled here and never reach the SPA fallback catch-all ({*path}).
var distFullPath = Path.GetFullPath(appConfig.DistDir);
if (Directory.Exists(distFullPath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(distFullPath),
        RequestPath = ""
    });
}

app.UseCors();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// Phase 16 middleware: MaxAllowed concurrent-request gate (mirrors Go's
// middlewares.MaxAllowed, wired when MaxConnections > 0), then download/upload
// rate-limit stream wrapping (mirrors middlewares.Download/UploadRateLimiter).
if (appConfig.MaxConnections > 0)
    app.UseMiddleware<MaxAllowedMiddleware>(appConfig.MaxConnections);
app.UseMiddleware<RateLimitMiddleware>(RateLimitDirection.Download);
app.UseMiddleware<RateLimitMiddleware>(RateLimitDirection.Upload);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    // Phase 16: Scalar API reference at /scalar — points at Swashbuckle's
    // swagger.json so it reuses the existing OpenAPI document.
    app.MapScalarApiReference(options =>
    {
        options.WithTitle("OpenList API");
        options.WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient);
        options.WithOpenApiRoutePattern("/swagger/v1/swagger.json");
    });
}

// Health check
app.MapHealthEndpoints();

// Phase 2: Auth + Me
app.MapAuthApi();
app.MapAuthnApi();
app.MapMeApi();
// Phase 3: Admin (storage + driver + user management)
app.MapAdminApi();
// Phase 4: Filesystem operations
app.MapFsApi();
// Phase 6: Task management
app.MapTaskApi();
// Phase 10: S3-compatible gateway
app.MapS3Api();
// Phase 11: WebDAV server
app.MapWebDavApi();
// Public settings + archive extensions
app.MapPublicApi();
// Signed download/proxy endpoints (/d/, /p/) — public, HMAC-verified
app.MapSignApi();
// Plist (iOS IPA manifest) — public
app.MapMiscApi();
// Sharing system (Phase 5)
app.MapShareApi();
// Sharing signed download (/sd/{sid}[/*path]) — public, share-verified
app.MapSharingDownloadApi();
// Multipart (chunked) upload — auth'd
app.MapMultipartApi();
// Torrent endpoints (parse / upload_parse / rapid_upload / generate) — auth'd
app.MapTorrentApi();
// MCP (Model Context Protocol) server — admin, gated by MCP.Enable
app.MapMcpApi();
// Debug endpoints (/debug/*) — Development only (mirrors Go flags.Debug || flags.Dev)
if (app.Environment.IsDevelopment())
{
    app.MapDebugApi();
}
// SPA fallback — matches ALL unmatched paths (including those with file extensions),
// mirroring Go's NoRoute catch-all. The default MapFallback uses {*path:nonfile}
// which excludes paths with extensions (e.g. /somefile.txt) and returns 404.
app.MapFallback("{*path}", ctx =>
{
    var distDir = appConfig.DistDir;
    var indexPath = Path.Combine(distDir, "index.html");
    if (File.Exists(indexPath))
    {
        ctx.Response.ContentType = "text/html";
        // Prevent caching of index.html so users always get the latest app shell
        ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        ctx.Response.Headers.Pragma = "no-cache";
        ctx.Response.Headers.Expires = "0";

        return ctx.Response.SendFileAsync(indexPath);
    }
    ctx.Response.StatusCode = 404;
    return Task.CompletedTask;
});

// Initialize database + seed admin user + load storages
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();
    // Development ⇒ 用 "admin" 作为内置管理员初始密码（对齐 Go 的 --dev），
    // 免去本地每次都走一遍 Web 安装向导。生产环境未配置 AdminPassword 时不建管理员，
    // 改由 /api/public/init/setup 在用户首次访问时创建。
    await DbInitializer.InitializeAsync(db, appConfig, app.Environment.IsDevelopment());

    // Load all enabled storages into the mount filesystem.
    var storageSvc = scope.ServiceProvider.GetRequiredService<IStorageService>();
    await storageSvc.LoadAllAsync();

    // Restore persisted search-index progress (Go bootstrap.InitIndex): re-enables
    // incremental updates after restart and recovers a stale "running" state.
    var searchSvc = scope.ServiceProvider.GetRequiredService<ISearchService>();
    await searchSvc.InitIndexAsync();

    // Start multipart session GC (Go multipart.DefaultManager.StartGC): sweep orphaned
    // temp chunks from a previous run and begin periodic expiry of idle sessions.
    if (scope.ServiceProvider.GetRequiredService<IMultipartUploadService>() is MultipartUploadService mp)
        mp.StartGc();

    // Start S3 multipart-upload GC (Go s3Backend.startReaper): sweep orphaned part dirs
    // from a previous run and begin periodic TTL reaping of abandoned uploads.
    scope.ServiceProvider.GetRequiredService<S3MultipartStore>().StartGc();
}

app.Run();

}
catch (Exception ex)
{
    Log.Fatal(ex, "OpenList server terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>
/// 程序入口 Program 类（partial，仅用于类型公开以支持集成测试中的
/// WebApplicationFactory<Program> 引用）。
/// </summary>
public partial class Program { }
