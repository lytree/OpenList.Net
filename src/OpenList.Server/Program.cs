using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Auth;
using OpenList.Infrastructure.Services;
using OpenList.Server.Endpoints;
using OpenList.Server.Middleware;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Configuration
var config = new AppConfiguration();
builder.Configuration.Bind(config);
if (string.IsNullOrEmpty(config.JwtSecret))
    config.JwtSecret = Guid.NewGuid().ToString("N")[..16];

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
    });
builder.Services.AddAuthorization();

// JSON options for Minimal APIs (PascalCase)
builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
});

// WebDAV HTTP methods support
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(options =>
{
    options.Limits.MaxRequestBodySize = 10L * 1024 * 1024 * 1024; // 10GB
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

// Initialize default settings
using (var scope = app.Services.CreateScope())
{
    var settings = scope.ServiceProvider.GetRequiredService<ISettingService>();
    await settings.DefaultSettingsAsync();
}

// Load storages
using (var scope = app.Services.CreateScope())
{
    var storageService = scope.ServiceProvider.GetRequiredService<IStorageService>();
    await storageService.LoadAllStoragesAsync();
}

// Middleware
app.UseCors();
app.UseMiddleware<CorsMiddleware>();
app.UseMiddleware<RateLimitMiddleware>();
app.UseMiddleware<SignMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

// Swagger
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Static files for frontend
if (!string.IsNullOrEmpty(config.DistDir) && Directory.Exists(config.DistDir))
{
    app.UseDefaultFiles(config.DistDir);
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(config.DistDir),
        RequestPath = ""
    });
}

// Health check
app.MapGet("/ping", () => Results.Json(new { code = 200, message = "pong" })).AllowAnonymous();

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
if (!string.IsNullOrEmpty(config.DistDir) && Directory.Exists(config.DistDir))
{
    app.MapFallbackToFile("index.html");
}

app.Run();
