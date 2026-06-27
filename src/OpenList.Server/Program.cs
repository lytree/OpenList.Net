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
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Services;
using OpenList.Server.Endpoints;
using OpenList.Server.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Add environment variables with OpenList__ prefix (overrides appsettings.json)
builder.Configuration.AddEnvironmentVariables("OpenList__");

// Bind configuration (OpenList__ prefix env vars override)
builder.Services.Configure<AppConfiguration>(builder.Configuration);
var appConfig = builder.Configuration.Get<AppConfiguration>() ?? new AppConfiguration();

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
builder.Services.AddSingleton<ZioMountService>();
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
// SignService must be singleton (matches ZioMountService lifetime) so FileSystemService
// can resolve it. Uses the same JWT secret for HMAC signing.
builder.Services.AddSingleton<ISignService, SignService>();
// Offline-download tool adapters (aria2 / qBittorrent / Transmission) talk to external
// daemons over HTTP — register a named HttpClient per tool so timeouts/policies can be
// tuned independently. IHttpClientFactory is the standard ASP.NET Core pattern.
builder.Services.AddHttpClient("offline_download_aria2");
builder.Services.AddHttpClient("offline_download_qbit");
builder.Services.AddHttpClient("offline_download_transmission");
// IOfflineDownloadService is a singleton because it only depends on singletons
// (ITaskService, IFileSystemService, IHttpClientFactory) + IServiceScopeFactory for
// the scoped ISettingService lookups.
builder.Services.AddSingleton<IOfflineDownloadService, OfflineDownloadService>();
// FileSystemService is a singleton; the new 3-arg constructor wires in ISignService
// and IOfflineDownloadService (both singletons) so AddOfflineDownloadAsync can delegate.
builder.Services.AddSingleton<IFileSystemService>(sp =>
    ActivatorUtilities.CreateInstance<FileSystemService>(sp,
        sp.GetRequiredService<ZioMountService>(),
        sp.GetService<ISignService>(),
        sp.GetService<IOfflineDownloadService>()));
// TODO: register IShareService, etc. (Phase 5+)

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
    .AddSingleton<FubarDev.FtpServer.FileSystem.IFileSystemClassFactory, ZioFtpFileSystemFactory>());
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

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
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
    await DbInitializer.InitializeAsync(db, appConfig);

    // Load all enabled storages into the mount filesystem.
    var storageSvc = scope.ServiceProvider.GetRequiredService<IStorageService>();
    await storageSvc.LoadAllAsync();
}

app.Run();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program { }
