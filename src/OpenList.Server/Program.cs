using System.Text.Json;
using FubarDev.FtpServer;
using FubarDev.FtpServer.AccountManagement;
using FubarDev.FtpServer.FileSystem;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
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
builder.Services.AddSingleton(appConfig.Database);
builder.Services.AddDbContext<OpenListDbContext>(opts =>
{
    if (appConfig.Database.Type.Equals("postgres", StringComparison.OrdinalIgnoreCase) ||
        appConfig.Database.Type.Equals("postgresql", StringComparison.OrdinalIgnoreCase))
    {
        // TODO (Phase 16): configure Npgsql with connection string
    }
    else
    {
        opts.UseSqlite($"Data Source={appConfig.Database.DBFile}");
    }
});

// Core services
builder.Services.AddSingleton<ZioMountService>();
builder.Services.AddSingleton<IUserService, UserService>();
builder.Services.AddSingleton<IAuthService, JwtService>();
builder.Services.AddSingleton<IStorageService, StorageService>();
builder.Services.AddSingleton<IFileSystemService, FileSystemService>();
builder.Services.AddSingleton<ISettingService, SettingService>();
builder.Services.AddSingleton<IMetaService, MetaService>();
builder.Services.AddSingleton<IMessageService, MessageService>();
builder.Services.AddSingleton<IScanService, ScanService>();
// TODO: register IShareService, ITaskService, etc. (Phase 5+)

// JWT auth — frontend sends token WITHOUT "Bearer " prefix.
var jwtSecret = string.IsNullOrWhiteSpace(appConfig.JwtSecret)
    ? (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"))[..32]
    : appConfig.JwtSecret;
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
    .AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

// Kestrel: 10 GB body limit for WebDAV uploads
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 10L * 1024 * 1024 * 1024);

// Phase 12: FTP/SFTP hosted servers (disabled by default via FTP:Enable / SFTP:Enable)
builder.Services.AddFtpServer(s => s
    .Services
    .AddSingleton<FubarDev.FtpServer.AccountManagement.IMembershipProvider, OpenListFtpMembershipProvider>()
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
app.MapMeApi();
// Phase 3: Admin (storage + driver + user management)
app.MapAdminApi();
// Phase 4: Filesystem operations
app.MapFsApi();
// Phase 10: S3-compatible gateway
app.MapS3Api();
// Phase 11: WebDAV server
app.MapWebDavApi();
// TODO: MapShareApi, MapTaskApi, MapPublicApi, MapSignApi,
//       MapSharingDownloadApi (Phase 5+)

// SPA fallback
app.MapFallback(ctx =>
{
    var distDir = appConfig.DistDir;
    var indexPath = Path.Combine(distDir, "index.html");
    if (File.Exists(indexPath))
    {
        ctx.Response.ContentType = "text/html";
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
