using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Configuration;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Services;
using OpenList.Server.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// Bind configuration (OpenList__ prefix env vars override)
builder.Services.Configure<AppConfiguration>(builder.Configuration);
var appConfig = builder.Configuration.Get<AppConfiguration>() ?? new AppConfiguration();

// JSON: camelCase, case-insensitive
builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

// Database
builder.Services.AddDbContext<OpenListDbContext>(opts =>
{
    if (appConfig.Database.Type.Equals("postgres", StringComparison.OrdinalIgnoreCase) ||
        appConfig.Database.Type.Equals("postgresql", StringComparison.OrdinalIgnoreCase))
    {
        // TODO: configure Npgsql with connection string
    }
    else
    {
        opts.UseSqlite($"Data Source={appConfig.Database.DBFile}");
    }
});

// Core services
builder.Services.AddSingleton<ZioMountService>();
// TODO: register IAuthService, IFileSystemService, IUserService, etc. (Phase 2+)

// CORS
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

// Kestrel: 10 GB body limit for WebDAV uploads
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 10L * 1024 * 1024 * 1024);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseCors();
app.UseRouting();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Health check
app.MapHealthEndpoints();

// TODO: register MapAuthApi, MapFsApi, MapAdminApi, MapShareApi, MapTaskApi,
//       MapMeApi, MapPublicApi, MapSignApi, MapSharingDownloadApi, MapS3Api, MapWebDavApi
//       (Phase 2+ — see README for implementation plan)

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

app.Run();
