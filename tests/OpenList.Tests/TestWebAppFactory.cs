using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Services;

namespace OpenList.Tests;

/// <summary>
/// WebApplicationFactory for integration tests of S3/WebDAV endpoints.
/// Uses a temp SQLite database and a temp directory mounted at /test via LocalDriver.
/// The default admin user (admin/admin) is seeded by DbInitializer.
/// </summary>
public sealed class TestWebAppFactory : WebApplicationFactory<Program>
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "OpenListTest_" + Guid.NewGuid().ToString("N"));
    private readonly string _dbFile = Path.Combine(Path.GetTempPath(), "OpenListTest_" + Guid.NewGuid().ToString("N") + ".db");

    /// <summary>Basic auth header value for the seeded admin user (admin:admin).</summary>
    public static readonly string BasicAuth = "Basic " + Convert.ToBase64String("admin:admin"u8.ToArray());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Use environment variables so Program.cs reads them via builder.Configuration
        // (ConfigureAppConfiguration runs too late — appConfig is captured first).
        Environment.SetEnvironmentVariable("OpenList__Database__DBFile", _dbFile);
        Environment.SetEnvironmentVariable("OpenList__FTP__Enable", "false");
        Environment.SetEnvironmentVariable("OpenList__SFTP__Enable", "false");

        builder.UseEnvironment("Development");
        builder.UseDefaultServiceProvider(o =>
        {
            o.ValidateScopes = false;
            o.ValidateOnBuild = false;
        });
    }

    /// <summary>Seed a Local storage at /test and mount it. Call after CreateClient.</summary>
    public async Task SeedStorageAsync()
    {
        Directory.CreateDirectory(_tempDir);
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();
        var mount = scope.ServiceProvider.GetRequiredService<ZioMountService>();

        var storage = new Storage
        {
            MountPath = "/test",
            Driver = "Local",
            Addition = $"{{\"root_folder_path\":\"{_tempDir.Replace("\\", "\\\\")}\"}}",
            Order = 0,
            Disabled = false,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Storages.Add(storage);
        await db.SaveChangesAsync();
        await mount.LoadAsync(storage);
    }

    /// <summary>Write a file into the mounted temp directory.</summary>
    public async Task WriteFileAsync(string relativePath, byte[] content)
    {
        var fullPath = Path.Combine(_tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, content);
    }

    /// <summary>
    /// Build a valid ZIP archive containing a single textual entry. The LocalDriver now
    /// implements the archive interfaces (SharpCompress), so archive endpoints expect a
    /// real archive rather than a bare magic-header stub.
    /// </summary>
    public static byte[] CreateZipBytes(string entryName = "hello.txt", string content = "hello")
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry(entryName);
            using var entryStream = entry.Open();
            using var writer = new StreamWriter(entryStream);
            writer.Write(content);
        }
        return ms.ToArray();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Environment.SetEnvironmentVariable("OpenList__Database__DBFile", null);
            Environment.SetEnvironmentVariable("OpenList__FTP__Enable", null);
            Environment.SetEnvironmentVariable("OpenList__SFTP__Enable", null);
            try { if (File.Exists(_dbFile)) File.Delete(_dbFile); } catch { }
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }
        base.Dispose(disposing);
    }
}
