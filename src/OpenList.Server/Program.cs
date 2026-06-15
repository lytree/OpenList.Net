using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Fido2NetLib;
using Fido2NetLib.Objects;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Auth;
using OpenList.Infrastructure.Services;
using OpenList.Server;
using OpenList.Server.Endpoints;
using OpenList.Server.Middleware;
using Serilog;
using System.Text;

// Configure Mapster type adapter mappings
MappingConfig.Configure();

// Configure Serilog before building the host
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
        .AddEnvironmentVariables()
        .Build())
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}{NewLine}{Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: "data/logs/openlist-.log",
        rollingInterval: RollingInterval.Day,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}{NewLine}{Message:lj}{NewLine}{Exception}",
        retainedFileCountLimit: 30,
        fileSizeLimitBytes: 50 * 1024 * 1024)
    .CreateLogger();

try
{
    Log.Information("Starting OpenList.Net");

var builder = WebApplication.CreateBuilder(args);

// Use Serilog for logging
builder.Host.UseSerilog();

// Configuration
var config = new AppConfiguration();
builder.Configuration.Bind(config);
if (string.IsNullOrEmpty(config.JwtSecret))
    config.JwtSecret = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

// Configuration validation
var configErrors = new List<string>();
if (config.MaxConnections < 0) configErrors.Add("MaxConnections must be >= 0");
if (config.Database?.Port is int p and (> 0 and (< 1 or > 65535))) configErrors.Add("Database.Port must be between 1 and 65535");
if (configErrors.Count > 0)
{
    var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("ConfigValidation");
    foreach (var err in configErrors) logger.LogError("Configuration error: {Error}", err);
    throw new InvalidOperationException($"Invalid configuration: {string.Join("; ", configErrors)}");
}

builder.Services.AddSingleton(config);

// Database
builder.Services.AddOpenListDbContext(config.Database);

// Services
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IStorageService, StorageService>();
builder.Services.AddScoped<IMetaService, MetaService>();
builder.Services.AddScoped<ISettingService, SettingService>();
builder.Services.AddScoped<ISharingService, SharingService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ITaskService, TaskService>();
builder.Services.AddScoped<ISignService, SignService>();
builder.Services.AddScoped<IFileSystemService, FileSystemService>();
builder.Services.AddSingleton<IDriverFactory, DriverFactory>();
builder.Services.AddSingleton<ISearchService, SearchService>();
builder.Services.AddScoped<IMessageService, MessageService>();
builder.Services.AddScoped<IScanService, ScanService>();
builder.Services.AddScoped<OfflineDownloadService>();

// WebAuthn / FIDO2
builder.Services.AddScoped<WebAuthnService>();
builder.Services.AddSingleton<IFido2>(sp =>
{
    var origin = string.IsNullOrEmpty(config.SiteURL)
        ? $"http://localhost:{config.Scheme.HttpPort}"
        : config.SiteURL.TrimEnd('/');
    return new Fido2(new Fido2Configuration
    {
        ServerDomain = new Uri(origin).Host,
        ServerName = "OpenList",
        Origins = new HashSet<string> { origin },
        TimestampDriftTolerance = 300000
    });
});

// Authentication
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config.JwtSecret)),
            ValidateIssuer = false,
            ValidateAudience = false,
            ClockSkew = TimeSpan.Zero
        };
        // Frontend sends token without "Bearer " prefix
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var token = context.Request.Headers.Authorization.ToString();
                if (!string.IsNullOrEmpty(token) && !token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    context.Token = token;
                }
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

// JSON options for Minimal APIs (camelCase to match frontend)
builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

// WebDAV HTTP methods support
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(options =>
{
    options.Limits.MaxRequestBodySize = 10L * 1024 * 1024 * 1024; // 10GB

    // HTTPS configuration
    if (config.Scheme.HttpsPort > 0 && !string.IsNullOrEmpty(config.Scheme.CertFile) && File.Exists(config.Scheme.CertFile))
    {
        options.ListenAnyIP(config.Scheme.HttpPort > 0 ? config.Scheme.HttpPort : 80);
        options.ListenAnyIP(config.Scheme.HttpsPort, listenOptions =>
        {
            listenOptions.UseHttps(config.Scheme.CertFile, config.Scheme.CertPassword ?? "");
        });
    }
});

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

// Cache
builder.Services.AddMemoryCache();

var app = builder.Build();

// Initialize database
await app.Services.InitializeDatabaseAsync();

// Initialize default settings and admin user
using (var scope = app.Services.CreateScope())
{
    var settings = scope.ServiceProvider.GetRequiredService<ISettingService>();
    await settings.DefaultSettingsAsync();

    var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
    var existingAdmin = await userService.GetUserByNameAsync(config.AdminUsername);
    if (existingAdmin == null)
    {
        var adminUser = new User
        {
            Username = config.AdminUsername,
            Role = UserRole.Admin,
            Permission = -1,
            BasePath = "/"
        };
        adminUser = await userService.CreateUserAsync(adminUser);
        await userService.SetPasswordAsync(adminUser, config.AdminPassword);
        var initLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        initLogger.LogInformation("Initial admin user '{Username}' created", config.AdminUsername);
    }
}

// Load storages
using (var scope = app.Services.CreateScope())
{
    var storageService = scope.ServiceProvider.GetRequiredService<IStorageService>();
    await storageService.LoadAllStoragesAsync();
}
StoragesLoadedMiddleware.MarkLoaded();

// Middleware
app.UseCors();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseMiddleware<CorsMiddleware>();
if (config.Scheme.ForceHttps && config.Scheme.HttpPort != -1 && config.Scheme.HttpsPort != -1)
    app.UseMiddleware<ForceHttpsMiddleware>();
if (config.MaxConnections > 0)
    app.UseMiddleware<MaxConnectionsMiddleware>();
app.UseMiddleware<StoragesLoadedMiddleware>();
app.UseMiddleware<FsUpMiddleware>();
app.UseMiddleware<RateLimitMiddleware>();
app.UseMiddleware<DownloadRateLimiterMiddleware>();
app.UseMiddleware<UploadRateLimiterMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/dav"), builder =>
    builder.UseMiddleware<WebDavAuthMiddleware>());

// Swagger
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Static files for frontend
if (!string.IsNullOrEmpty(config.DistDir))
{
    var distPath = Path.GetFullPath(config.DistDir);
    if (Directory.Exists(distPath))
    {
        var distFileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(distPath);
        app.UseDefaultFiles(new DefaultFilesOptions
        {
            FileProvider = distFileProvider,
            RequestPath = ""
        });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = distFileProvider,
            RequestPath = ""
        });
    }
}

// Health check
app.MapGet("/ping", () => Results.Text("pong")).AllowAnonymous();

// Static meta endpoints
app.MapGet("/favicon.ico", (ISettingService settings) =>
{
    var favicon = settings.GetString("favicon");
    if (!string.IsNullOrEmpty(favicon) && Uri.TryCreate(favicon, UriKind.Absolute, out var uri))
        return Results.Redirect(uri.ToString());
    return Results.NoContent();
}).AllowAnonymous();

app.MapGet("/robots.txt", (ISettingService settings) =>
{
    var robotsTxt = settings.GetString("robots_txt");
    return string.IsNullOrEmpty(robotsTxt)
        ? Results.Text("User-agent: *\nAllow: /\n", "text/plain")
        : Results.Text(robotsTxt, "text/plain");
}).AllowAnonymous();

app.MapGet("/manifest.json", (ISettingService settings) =>
    Results.Json(new
    {
        name = settings.GetString("title") ?? "OpenList",
        display = "standalone",
        scope = "/",
        start_url = "/",
        icons = new[]
        {
            new { src = (settings.GetString("logo") ?? "").Split('\n')[0], sizes = "512x512", type = "image/png" }
        }
    }, statusCode: 200)).AllowAnonymous();

// Minimal API endpoints
app.MapAuthApi();
app.MapFsApi();
app.MapAdminApi();
app.MapShareApi();
app.MapTaskApi();
app.MapMeApi();
app.MapPublicApi();
app.MapSignRoutes();
app.MapSharingDownloadRoutes();
app.MapS3Api();
app.MapWebDavApi();

// Fallback for SPA
if (!string.IsNullOrEmpty(config.DistDir))
{
    var distPath = Path.GetFullPath(config.DistDir);
    if (Directory.Exists(distPath))
    {
        app.MapFallbackToFile("index.html", new StaticFileOptions
        {
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(distPath)
        });
    }
}

// Graceful shutdown
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    Log.Information("Application is shutting down gracefully...");
});

app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
