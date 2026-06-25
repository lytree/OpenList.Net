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

        // EnsureCreated only creates the schema once; for databases created
        // before new columns were added to the model, patch the schema with
        // additive ALTER TABLE statements (SQLite supports ADD COLUMN).
        await MigrateSchemaAsync(db, ct);

        await SeedAdminAsync(db, config, ct);
        await SeedDefaultSettingsAsync(db, ct);
    }

    /// <summary>
    /// Lightweight additive schema migration for databases created by
    /// <see cref="DatabaseFacade.EnsureCreatedAsync"/>. Adds columns that were
    /// introduced after the initial schema without requiring EF migrations.
    /// SQLite's ALTER TABLE ADD COLUMN is non-destructive and idempotent when
    /// guarded by a column-existence check.
    /// </summary>
    private static async Task MigrateSchemaAsync(OpenListDbContext db, CancellationToken ct)
    {
        // Columns to add: (table, column, sqlite type declaration).
        // Only additive changes are safe with ALTER TABLE ADD COLUMN.
        // SsoID already exists as INTEGER (from bool); SQLite's dynamic typing
        // allows TEXT values in INTEGER columns, so no change is needed there.
        var additions = new (string Table, string Column, string Decl)[]
        {
            ("x_users", "AllowLdap", "INTEGER NOT NULL DEFAULT 0"),
            ("x_storages", "Status", "TEXT NOT NULL DEFAULT 'work'"),
            // Sharing: extended fields mirroring Go model.Sharing.
            ("x_sharings", "ShareCode", "TEXT NOT NULL DEFAULT ''"),
            ("x_sharings", "FilesJson", "TEXT NOT NULL DEFAULT '[]'"),
            ("x_sharings", "MaxAccessed", "INTEGER NOT NULL DEFAULT 0"),
            ("x_sharings", "Accessed", "INTEGER NOT NULL DEFAULT 0"),
            ("x_sharings", "Disabled", "INTEGER NOT NULL DEFAULT 0"),
            ("x_sharings", "Remark", "TEXT NULL"),
            ("x_sharings", "Readme", "TEXT NULL"),
            ("x_sharings", "Header", "TEXT NULL"),
            ("x_sharings", "OrderBy", "TEXT NULL"),
            ("x_sharings", "OrderDirection", "TEXT NULL"),
            ("x_sharings", "ExtractFolder", "TEXT NULL"),
            // SshKey: last-used timestamp for the frontend's last_used_time column.
            ("x_sshkeys", "LastUsedTime", "TEXT NOT NULL DEFAULT '0001-01-01T00:00:00+00:00'"),
            // Meta: extended fields mirroring the frontend Meta type.
            ("x_metas", "ReadUsersJson", "TEXT NOT NULL DEFAULT '[]'"),
            ("x_metas", "ReadUsersSub", "INTEGER NOT NULL DEFAULT 0"),
            ("x_metas", "WriteUsersJson", "TEXT NOT NULL DEFAULT '[]'"),
            ("x_metas", "WriteUsersSub", "INTEGER NOT NULL DEFAULT 0"),
            ("x_metas", "WSub", "INTEGER NOT NULL DEFAULT 0"),
            ("x_metas", "HideSub", "INTEGER NOT NULL DEFAULT 0"),
            ("x_metas", "RSub", "INTEGER NOT NULL DEFAULT 0"),
            ("x_metas", "HeaderSub", "INTEGER NOT NULL DEFAULT 0"),
        };

        foreach (var (table, column, decl) in additions)
        {
            if (await ColumnExistsAsync(db, table, column, ct))
                continue;
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE {table} ADD COLUMN {column} {decl};", ct);
            }
            catch
            {
                // Column may already exist (race) or table missing — ignore.
            }
        }
    }

    private static async Task<bool> ColumnExistsAsync(
        OpenListDbContext db, string table, string column, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        var rows = new List<string>();
        await conn.OpenAsync(ct);
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                // Column name is at ordinal 1 in PRAGMA table_info output.
                rows.Add(reader.GetString(1));
            }
        }
        finally
        {
            await conn.CloseAsync();
        }
        return rows.Contains(column, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task SeedAdminAsync(OpenListDbContext db, AppConfiguration config, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        // Only override the password when a non-empty value is provided in config.
        // An empty AdminPassword means "leave the existing password untouched".
        var hasConfigPassword = !string.IsNullOrWhiteSpace(config.AdminPassword);
        var desiredHash = hasConfigPassword
            ? UserService.HashPassword(config.AdminPassword)
            : null;

        // The config-defined super admin: identified by the configured username.
        // On every startup we reconcile it with the configuration so that changing
        // AdminUsername / AdminPassword in config propagates without a re-seed.
        var admin = await db.Users.FirstOrDefaultAsync(u => u.Username == config.AdminUsername, ct);

        if (admin is null)
        {
            // No user with the configured username yet. If another admin exists
            // (e.g. the username was renamed in config), adopt the first admin row.
            admin = await db.Users.FirstOrDefaultAsync(u => u.Role == UserRole.Admin, ct);
        }

        if (admin is null)
        {
            // First run — create the super admin from config.
            db.Users.Add(new User
            {
                Username = config.AdminUsername,
                PasswordHash = desiredHash ?? UserService.HashPassword("admin"),
                Role = UserRole.Admin,
                Permission = 0,
                Disabled = false,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            // Sync credentials + role from config on every startup.
            admin.Username = config.AdminUsername;
            // Only overwrite the password when config provides a non-empty value.
            if (desiredHash is not null)
                admin.PasswordHash = desiredHash;
            admin.Role = UserRole.Admin;
            admin.Disabled = false;
            admin.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Default settings keyed by group. Used both for initial seeding and for
    /// <c>POST /admin/setting/default?group=</c> (restore group to defaults).
    /// </summary>
    public static readonly (string Key, string Value, SettingValueType Type, SettingFlag Flag, string? Group)[] DefaultSettings =
    {
        // Group values are the frontend's Group enum (number-as-string):
        // 1=SITE, 2=STYLE, 6=INDEX. See frontend src/types/setting.ts.
        ("logo", "", SettingValueType.String, SettingFlag.Public, "2"),
        ("site_title", "OpenList", SettingValueType.String, SettingFlag.Public, "1"),
        ("announcement", "", SettingValueType.Text, SettingFlag.Public, "1"),
        ("home_container", "10", SettingValueType.Number, SettingFlag.Public, "2"),
        ("home_icon", "", SettingValueType.String, SettingFlag.Public, "2"),
        ("share_icon", "", SettingValueType.String, SettingFlag.Public, "2"),
        ("share_summary_content", "", SettingValueType.Text, SettingFlag.Public, "2"),
        ("search_index", "", SettingValueType.String, SettingFlag.Public, "6"),
        ("filter_readme_scripts", "false", SettingValueType.Bool, SettingFlag.Public, "2"),
    };

    /// <summary>
    /// Seeds the default public settings expected by the frontend. The SolidJS SPA
    /// reads these via <c>GET /api/public/settings</c> and calls methods like
    /// <c>.split('\n')</c> on the values, so every key must exist with at least an
    /// empty string to avoid <c>Cannot read properties of undefined</c> errors.
    /// </summary>
    private static async Task SeedDefaultSettingsAsync(OpenListDbContext db, CancellationToken ct)
    {
        // Migrate legacy group="ui" to the frontend's numeric group values,
        // and correct any type mismatches (e.g. announcement String → Text).
        var defaultsByKey = DefaultSettings.ToDictionary(d => d.Key, StringComparer.Ordinal);
        var allSettings = await db.Settings.ToListAsync(ct);
        foreach (var s in allSettings)
        {
            if (defaultsByKey.TryGetValue(s.Key, out var def))
            {
                if (s.Group != def.Group) s.Group = def.Group;
                if (s.Type != def.Type) s.Type = def.Type;
            }
        }

        var existingSet = new HashSet<string>(allSettings.Select(s => s.Key), StringComparer.Ordinal);

        foreach (var (key, value, type, flag, group) in DefaultSettings)
        {
            if (existingSet.Contains(key))
                continue;
            db.Settings.Add(new SettingItem
            {
                Key = key,
                Value = value,
                Type = type,
                Flag = flag,
                Group = group,
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
