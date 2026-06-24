using Microsoft.EntityFrameworkCore;
using OpenList.Core.Configuration;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Services;

namespace OpenList.Infrastructure.Data;

/// <summary>
/// Database initializer: migrate (or EnsureCreated fallback) + seed admin user on first run.
/// Mirrors OpenList Go <c>internal/db/init.go</c>.
/// </summary>
public static class DbInitializer
{
    public static async Task InitializeAsync(OpenListDbContext db, AppConfiguration config, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(config.Database.DBFile);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // Apply pending migrations if any are configured.
        try
        {
            var migrations = await db.Database.GetPendingMigrationsAsync(ct);
            if (migrations.Any())
            {
                await db.Database.MigrateAsync(ct);
                await SeedAdminAsync(db, config, ct);
                return;
            }
        }
        catch { /* no migrations assembly — fall through to EnsureCreated */ }

        // No migrations — create schema directly from the model.
        await db.Database.EnsureCreatedAsync(ct);

        await SeedAdminAsync(db, config, ct);
    }

    private static async Task SeedAdminAsync(OpenListDbContext db, AppConfiguration config, CancellationToken ct)
    {
        if (await db.Users.AnyAsync(u => u.Role == UserRole.Admin, ct)) return;

        var now = DateTimeOffset.UtcNow;
        db.Users.Add(new User
        {
            Username = config.AdminUsername,
            PasswordHash = UserService.HashPassword(config.AdminPassword),
            Role = UserRole.Admin,
            Permission = 0,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(ct);
    }
}
