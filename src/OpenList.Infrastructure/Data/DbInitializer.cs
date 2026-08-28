using Microsoft.EntityFrameworkCore;
using OpenList.Core.Configuration;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Services;

namespace OpenList.Infrastructure.Data;

/// <summary>
/// 数据库初始化器：负责执行数据库迁移（或回退到 EnsureCreated），
/// 并在首次运行时填充管理员用户与默认设置。对应 Go 参考实现 <c>internal/db/init.go</c>。
/// </summary>
public static class DbInitializer
{
    /// <summary>
    /// 初始化数据库结构并写入种子数据。顺序为：先尝试 EF Core 迁移（有挂起迁移时执行
    /// <see cref="DatabaseFacade.MigrateAsync"/>），迁移失败（无迁移程序集或旧版 EnsureCreated
    /// 库表冲突）则回退到 EnsureCreated + 追加式补列，最后写入管理员账号与默认设置。
    /// </summary>
    /// <param name="db">EF Core 数据库上下文。</param>
    /// <param name="config">应用配置，用于读取数据库类型、管理员账号与密码等。</param>
    /// <param name="ct">取消令牌（可选）。</param>
    /// <exception cref="Exception">任意数据库操作失败时抛出（由调用方决定是否放行）。</exception>
    public static async Task InitializeAsync(OpenListDbContext db, AppConfiguration config, CancellationToken ct = default)
    {
        var isPostgres = IsPostgres(config.Database.Type);

        // SQLite stores its database in a local file — make sure the parent
        // directory exists. PostgreSQL is a remote server; no file to create.
        if (!isPostgres)
        {
            var dir = Path.GetDirectoryName(config.Database.DBFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        // Prefer EF Core migrations (one assembly per provider — see
        // OpenListDbContextFactory + Program.cs MigrationsAssembly()).
        // Fresh DB: MigrateAsync creates the schema + __EFMigrationsHistory.
        // Already-migrated DB: GetPendingMigrations is empty, nothing to do.
        // Legacy EnsureCreated DB (tables exist, no __EFMigrationsHistory):
        //   GetPendingMigrations returns all migrations, MigrateAsync throws
        //   because the tables already exist — the catch falls through to the
        //   EnsureCreated + MigrateSchemaAsync path which is idempotent.
        var migrated = false;
        try
        {
            var pending = await db.Database.GetPendingMigrationsAsync(ct);
            if (pending.Any())
                await db.Database.MigrateAsync(ct);
            migrated = true;
        }
        catch
        {
            // No migration assembly registered, or a pre-migration EnsureCreated
            // database whose tables collide with the Init migration. Either way
            // fall back to the legacy EnsureCreated + additive ALTER TABLE path.
            migrated = false;
        }

        if (!migrated)
        {
            // Legacy path: create schema directly from the model (no-op if the
            // database already exists) then patch additive columns.
            await db.Database.EnsureCreatedAsync(ct);
            await MigrateSchemaAsync(db, isPostgres, ct);
        }

        // Seed on BOTH paths — previously the MigrateAsync path skipped
        // SeedDefaultSettingsAsync, leaving fresh databases without defaults.
        await SeedAdminAsync(db, config, ct);
        await SeedDefaultSettingsAsync(db, ct);
    }

    /// <summary>
    /// 针对由 <see cref="DatabaseFacade.EnsureCreatedAsync"/> 创建的数据库执行轻量级的追加式
    /// 结构迁移：为初始表补充后续引入的列（ALTER TABLE ADD COLUMN 非破坏性、通过“列存在性”检查
    /// 实现幂等），并确保新增的表存在。同时支持 SQLite 与 PostgreSQL。
    /// </summary>
    /// <param name="db">EF Core 数据库上下文。</param>
    /// <param name="isPostgres">是否使用 PostgreSQL（否则按 SQLite 处理）。</param>
    /// <param name="ct">取消令牌。</param>
    private static async Task MigrateSchemaAsync(OpenListDbContext db, bool isPostgres, CancellationToken ct)
    {
        // Columns to add: (table, column, sqlite declaration, postgres declaration).
        // Only additive changes are safe with ALTER TABLE ADD COLUMN.
        // SsoID already exists as INTEGER (from bool); SQLite's dynamic typing
        // allows TEXT values in INTEGER columns, so no change is needed there.
        var additions = new (string Table, string Column, string SqliteDecl, string PgDecl)[]
        {
            ("x_users", "AllowLdap", "INTEGER NOT NULL DEFAULT 0", "BOOLEAN NOT NULL DEFAULT FALSE"),
            ("x_storages", "Status", "TEXT NOT NULL DEFAULT 'work'", "TEXT NOT NULL DEFAULT 'work'"),
            // Storage: extended fields mirroring Go model.Storage / frontend Storage type.
            ("x_storages", "OrderBy", "TEXT NULL", "TEXT NULL"),
            ("x_storages", "OrderDirection", "TEXT NULL", "TEXT NULL"),
            ("x_storages", "ExtractFolder", "TEXT NULL", "TEXT NULL"),
            ("x_storages", "WebProxy", "INTEGER NOT NULL DEFAULT 0", "BOOLEAN NOT NULL DEFAULT FALSE"),
            ("x_storages", "WebdavPolicy", "TEXT NULL", "TEXT NULL"),
            ("x_storages", "EnableSign", "INTEGER NOT NULL DEFAULT 0", "BOOLEAN NOT NULL DEFAULT FALSE"),
            ("x_storages", "Modified", "TEXT NOT NULL DEFAULT '0001-01-01T00:00:00+00:00'", "TIMESTAMPTZ NOT NULL DEFAULT '0001-01-01T00:00:00+00:00'"),
            // Sharing: extended fields mirroring Go model.Sharing.
            ("x_sharings", "ShareCode", "TEXT NOT NULL DEFAULT ''", "TEXT NOT NULL DEFAULT ''"),
            ("x_sharings", "FilesJson", "TEXT NOT NULL DEFAULT '[]'", "TEXT NOT NULL DEFAULT '[]'"),
            ("x_sharings", "MaxAccessed", "INTEGER NOT NULL DEFAULT 0", "INTEGER NOT NULL DEFAULT 0"),
            ("x_sharings", "Accessed", "INTEGER NOT NULL DEFAULT 0", "INTEGER NOT NULL DEFAULT 0"),
            ("x_sharings", "Disabled", "INTEGER NOT NULL DEFAULT 0", "BOOLEAN NOT NULL DEFAULT FALSE"),
            ("x_sharings", "Remark", "TEXT NULL", "TEXT NULL"),
            ("x_sharings", "Readme", "TEXT NULL", "TEXT NULL"),
            ("x_sharings", "Header", "TEXT NULL", "TEXT NULL"),
            ("x_sharings", "OrderBy", "TEXT NULL", "TEXT NULL"),
            ("x_sharings", "OrderDirection", "TEXT NULL", "TEXT NULL"),
            ("x_sharings", "ExtractFolder", "TEXT NULL", "TEXT NULL"),
            // SshKey: last-used timestamp for the frontend's last_used_time column.
            ("x_sshkeys", "LastUsedTime", "TEXT NOT NULL DEFAULT '0001-01-01T00:00:00+00:00'", "TIMESTAMPTZ NOT NULL DEFAULT '0001-01-01T00:00:00+00:00'"),
            // Meta: extended fields mirroring the frontend Meta type.
            ("x_metas", "ReadUsersJson", "TEXT NOT NULL DEFAULT '[]'", "TEXT NOT NULL DEFAULT '[]'"),
            ("x_metas", "ReadUsersSub", "INTEGER NOT NULL DEFAULT 0", "INTEGER NOT NULL DEFAULT 0"),
            ("x_metas", "WriteUsersJson", "TEXT NOT NULL DEFAULT '[]'", "TEXT NOT NULL DEFAULT '[]'"),
            ("x_metas", "WriteUsersSub", "INTEGER NOT NULL DEFAULT 0", "INTEGER NOT NULL DEFAULT 0"),
            ("x_metas", "WSub", "INTEGER NOT NULL DEFAULT 0", "INTEGER NOT NULL DEFAULT 0"),
            ("x_metas", "HideSub", "INTEGER NOT NULL DEFAULT 0", "INTEGER NOT NULL DEFAULT 0"),
            ("x_metas", "RSub", "INTEGER NOT NULL DEFAULT 0", "INTEGER NOT NULL DEFAULT 0"),
            ("x_metas", "HeaderSub", "INTEGER NOT NULL DEFAULT 0", "INTEGER NOT NULL DEFAULT 0"),
        };

        foreach (var (table, column, sqliteDecl, pgDecl) in additions)
        {
            if (await ColumnExistsAsync(db, table, column, isPostgres, ct))
                continue;
            var decl = isPostgres ? pgDecl : sqliteDecl;
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

        // Ensure whole tables introduced after the initial EnsureCreated run exist.
        // EnsureCreatedAsync only creates tables on a fresh database; for databases
        // created earlier we CREATE TABLE IF NOT EXISTS.
        if (isPostgres)
        {
            await EnsureTableAsync(db, "x_search_index", isPostgres, """
                CREATE TABLE IF NOT EXISTS x_search_index (
                    Id BIGSERIAL PRIMARY KEY,
                    Parent TEXT NOT NULL DEFAULT '',
                    Name TEXT NOT NULL DEFAULT '',
                    IsDir BOOLEAN NOT NULL DEFAULT FALSE,
                    Size BIGINT NOT NULL DEFAULT 0,
                    Path TEXT NOT NULL DEFAULT '',
                    Type INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS ix_x_search_index_Name ON x_search_index (Name);
                CREATE INDEX IF NOT EXISTS ix_x_search_index_Parent ON x_search_index (Parent);
                CREATE UNIQUE INDEX IF NOT EXISTS ix_x_search_index_Path ON x_search_index (Path);
                """, ct);

            await EnsureTableAsync(db, "x_authn_credentials", isPostgres, """
                CREATE TABLE IF NOT EXISTS x_authn_credentials (
                    Id BIGSERIAL PRIMARY KEY,
                    UserId BIGINT NOT NULL,
                    CredentialId TEXT NOT NULL,
                    UserHandle TEXT NOT NULL,
                    PublicKey TEXT NOT NULL,
                    SignCount INTEGER NOT NULL DEFAULT 0,
                    Aaguid TEXT NOT NULL DEFAULT '',
                    Transports TEXT NOT NULL DEFAULT '',
                    Discoverable BOOLEAN NOT NULL DEFAULT FALSE,
                    CreatedAt TIMESTAMPTZ NOT NULL DEFAULT '0001-01-01T00:00:00+00:00'
                );
                CREATE INDEX IF NOT EXISTS ix_x_authn_credentials_UserId ON x_authn_credentials (UserId);
                CREATE UNIQUE INDEX IF NOT EXISTS ix_x_authn_credentials_UserId_CredentialId ON x_authn_credentials (UserId, CredentialId);
                """, ct);
        }
        else
        {
            await EnsureTableAsync(db, "x_search_index", isPostgres, """
                CREATE TABLE IF NOT EXISTS x_search_index (
                    Id INTEGER NOT NULL CONSTRAINT PK_x_search_index PRIMARY KEY AUTOINCREMENT,
                    Parent TEXT NOT NULL DEFAULT '',
                    Name TEXT NOT NULL DEFAULT '',
                    IsDir INTEGER NOT NULL DEFAULT 0,
                    Size INTEGER NOT NULL DEFAULT 0,
                    Path TEXT NOT NULL DEFAULT '',
                    Type INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS ix_x_search_index_Name ON x_search_index (Name);
                CREATE INDEX IF NOT EXISTS ix_x_search_index_Parent ON x_search_index (Parent);
                CREATE UNIQUE INDEX IF NOT EXISTS ix_x_search_index_Path ON x_search_index (Path);
                """, ct);

            await EnsureTableAsync(db, "x_authn_credentials", isPostgres, """
                CREATE TABLE IF NOT EXISTS x_authn_credentials (
                    Id INTEGER NOT NULL CONSTRAINT PK_x_authn_credentials PRIMARY KEY AUTOINCREMENT,
                    UserId INTEGER NOT NULL,
                    CredentialId TEXT NOT NULL,
                    UserHandle TEXT NOT NULL,
                    PublicKey TEXT NOT NULL,
                    SignCount INTEGER NOT NULL DEFAULT 0,
                    Aaguid TEXT NOT NULL DEFAULT '',
                    Transports TEXT NOT NULL DEFAULT '',
                    Discoverable INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL DEFAULT '0001-01-01T00:00:00+00:00'
                );
                CREATE INDEX IF NOT EXISTS ix_x_authn_credentials_UserId ON x_authn_credentials (UserId);
                CREATE UNIQUE INDEX IF NOT EXISTS ix_x_authn_credentials_UserId_CredentialId ON x_authn_credentials (UserId, CredentialId);
                """, ct);
        }
    }

    /// <summary>
    /// 确保指定表存在：表不存在时执行给定的建表 DDL（含索引）。
    /// </summary>
    /// <param name="db">EF Core 数据库上下文。</param>
    /// <param name="table">表名。</param>
    /// <param name="isPostgres">是否使用 PostgreSQL（用于选择存在性查询）。</param>
    /// <param name="ddl">建表 SQL 语句。</param>
    /// <param name="ct">取消令牌。</param>
    private static async Task EnsureTableAsync(
        OpenListDbContext db, string table, bool isPostgres, string ddl, CancellationToken ct)
    {
        if (await TableExistsAsync(db, table, isPostgres, ct)) return;
        try { await db.Database.ExecuteSqlRawAsync(ddl, ct); }
        catch { /* race or already exists — ignore */ }
    }

    /// <summary>
    /// 判断指定表是否存在。SQLite 查询 sqlite_master，PostgreSQL 查询 information_schema.tables。
    /// </summary>
    private static async Task<bool> TableExistsAsync(
        OpenListDbContext db, string table, bool isPostgres, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        // SQLite: query sqlite_master; PostgreSQL: query information_schema.
        cmd.CommandText = isPostgres
            ? "SELECT 1 FROM information_schema.tables WHERE table_name = @t LIMIT 1;"
            : "SELECT name FROM sqlite_master WHERE type='table' AND name=@t;";
        var p = cmd.CreateParameter();
        p.ParameterName = "@t";
        p.Value = table;
        cmd.Parameters.Add(p);
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct);
        }
        finally { if (conn.State == System.Data.ConnectionState.Open) await conn.CloseAsync(); }
    }

    /// <summary>
    /// 判断指定列是否已存在。PostgreSQL 查询 information_schema.columns；SQLite 使用
    /// PRAGMA table_info 读取列名。
    /// </summary>
    private static async Task<bool> ColumnExistsAsync(
        OpenListDbContext db, string table, string column, bool isPostgres, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        if (isPostgres)
        {
            // PostgreSQL: query information_schema.columns.
            cmd.CommandText = "SELECT 1 FROM information_schema.columns WHERE table_name = @t AND column_name = @c LIMIT 1;";
            var pt = cmd.CreateParameter();
            pt.ParameterName = "@t";
            pt.Value = table;
            cmd.Parameters.Add(pt);
            var pc = cmd.CreateParameter();
            pc.ParameterName = "@c";
            pc.Value = column;
            cmd.Parameters.Add(pc);
        }
        else
        {
            // SQLite: PRAGMA table_info — column name is at ordinal 1.
            cmd.CommandText = $"PRAGMA table_info({table});";
        }

        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        try
        {
            if (isPostgres)
            {
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                return await reader.ReadAsync(ct);
            }
            else
            {
                var rows = new List<string>();
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    rows.Add(reader.GetString(1));
                return rows.Contains(column, StringComparer.OrdinalIgnoreCase);
            }
        }
        finally { if (conn.State == System.Data.ConnectionState.Open) await conn.CloseAsync(); }
    }

    /// <summary>
    /// 判断配置中的数据库类型是否为 PostgreSQL（同时识别 <c>postgres</c> 与 <c>postgresql</c>）。
    /// </summary>
    private static bool IsPostgres(string type)
        => type.Equals("postgres", StringComparison.OrdinalIgnoreCase) ||
           type.Equals("postgresql", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 首次运行时创建或校对超级管理员账号：依据配置的 AdminUsername 定位管理员，
    /// 不存在则创建，存在则同步用户名/角色/权限（密码仅在配置提供非空值时覆盖）。
    /// 管理员权限位全部置 1（共 16 位），使其拥有全部前端权限分项。
    /// </summary>
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
            // Permission: all 16 bits set (frontend UserPermissions has 16 entries;
            // userCan does not bypass for admins, so the super admin needs every bit).
            db.Users.Add(new User
            {
                Username = config.AdminUsername,
                PasswordHash = desiredHash ?? UserService.HashPassword("admin"),
                Role = UserRole.Admin,
                Permission = (1 << 16) - 1,
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
            admin.Permission = (1 << 16) - 1;
            admin.Disabled = false;
            admin.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 按分组组织的默认设置表：既用于首次装填，也用于 <c>POST /admin/setting/default?group=</c>
    /// 恢复某分组为默认值。Group 取值为前端 Group 枚举（int）：
    /// 0=SINGLE, 1=SITE, 2=STYLE, 3=PREVIEW, 4=GLOBAL, 5=ARIA2, 6=INDEX,
    /// 7=SSO, 8=LDAP, 9=S3, 10=FTP, 11=TRAFFIC。参见前端 src/types/setting.ts。
    /// </summary>
    public static readonly (string Key, string Value, SettingValueType Type, SettingFlag Flag, int Group, string? Options)[] DefaultSettings =
    {
        // --- Group 0: SINGLE (token + offline-download tool temp dirs, shown on /settings/other) ---
        ("token", "", SettingValueType.String, SettingFlag.Private, 0, null),
        ("webauthn_login_enabled", "false", SettingValueType.Bool, SettingFlag.Public, 0, null),
        ("115_temp_dir", "", SettingValueType.String, SettingFlag.Private, 0, null),
        ("115_open_temp_dir", "", SettingValueType.String, SettingFlag.Private, 0, null),
        ("123_pan_temp_dir", "", SettingValueType.String, SettingFlag.Private, 0, null),
        ("123_open_temp_dir", "", SettingValueType.String, SettingFlag.Private, 0, null),
        ("123_open_callback_url", "", SettingValueType.String, SettingFlag.Private, 0, null),
        ("pikpak_temp_dir", "", SettingValueType.String, SettingFlag.Private, 0, null),
        ("thunder_temp_dir", "", SettingValueType.String, SettingFlag.Private, 0, null),
        ("thunder_browser_temp_dir", "", SettingValueType.String, SettingFlag.Private, 0, null),
        ("thunderx_temp_dir", "", SettingValueType.String, SettingFlag.Private, 0, null),

        // --- Group 1: SITE ---
        ("site_title", "OpenList", SettingValueType.String, SettingFlag.Public, 1, null),
        ("announcement", "", SettingValueType.Text, SettingFlag.Public, 1, null),
        ("allow_indexed", "false", SettingValueType.Bool, SettingFlag.Public, 1, null),
        ("allow_mounted", "false", SettingValueType.Bool, SettingFlag.Public, 1, null),
        ("robots_txt", "", SettingValueType.Text, SettingFlag.Public, 1, null),

        // --- Group 2: STYLE ---
        ("logo", "", SettingValueType.String, SettingFlag.Public, 2, null),
        ("main_color", "#1890ff", SettingValueType.String, SettingFlag.Public, 2, null),
        ("home_icon", "", SettingValueType.String, SettingFlag.Public, 2, null),
        ("share_icon", "", SettingValueType.String, SettingFlag.Public, 2, null),
        ("share_summary_content", "", SettingValueType.Text, SettingFlag.Public, 2, null),
        ("home_container", "10", SettingValueType.Number, SettingFlag.Public, 2, null),
        ("settings_layout", "responsive", SettingValueType.Select, SettingFlag.Public, 2, "responsive,columns"),
        ("customize_head", "", SettingValueType.Text, SettingFlag.Public, 2, null),
        ("customize_body", "", SettingValueType.Text, SettingFlag.Public, 2, null),
        ("filter_readme_scripts", "false", SettingValueType.Bool, SettingFlag.Public, 2, null),
        ("show_disk_usage_in_plain_text", "false", SettingValueType.Bool, SettingFlag.Public, 2, null),
        ("hide_storage_details", "false", SettingValueType.Bool, SettingFlag.Public, 2, null),
        ("hide_storage_details_in_manage_page", "false", SettingValueType.Bool, SettingFlag.Public, 2, null),
        ("favicon", "", SettingValueType.String, SettingFlag.Public, 2, null),

        // --- Group 3: PREVIEW ---
        ("external_previews", "{}", SettingValueType.Text, SettingFlag.Public, 3, null),
        ("iframe_previews", "{}", SettingValueType.Text, SettingFlag.Public, 3, null),
        ("audio_types", "mp3,flac,m4a,wav,ogg,opus", SettingValueType.String, SettingFlag.Public, 3, null),
        ("video_types", "mp4,mkv,avi,mov,flv,wmv,webm", SettingValueType.String, SettingFlag.Public, 3, null),
        ("image_types", "jpg,jpeg,png,gif,webp,bmp,svg,ico", SettingValueType.String, SettingFlag.Public, 3, null),
        ("text_types", "txt,md,log,json,xml,yaml,yml,ini,conf,csv", SettingValueType.String, SettingFlag.Public, 3, null),
        ("audio_autoplay", "false", SettingValueType.Bool, SettingFlag.Public, 3, null),
        ("video_autoplay", "false", SettingValueType.Bool, SettingFlag.Public, 3, null),
        ("audio_cover", "", SettingValueType.String, SettingFlag.Public, 3, null),
        ("ocr_api", "", SettingValueType.String, SettingFlag.Public, 3, null),
        ("readme_autorender", "true", SettingValueType.Bool, SettingFlag.Public, 3, null),
        ("package_download", "false", SettingValueType.Bool, SettingFlag.Public, 3, null),
        ("share_preview", "true", SettingValueType.Bool, SettingFlag.Public, 3, null),
        ("share_archive_preview", "false", SettingValueType.Bool, SettingFlag.Public, 3, null),
        ("preview_archives_by_default", "false", SettingValueType.Bool, SettingFlag.Public, 3, null),
        ("preview_download_by_default", "false", SettingValueType.Bool, SettingFlag.Public, 3, null),
        ("share_preview_archives_by_default", "false", SettingValueType.Bool, SettingFlag.Public, 3, null),
        ("share_preview_download_by_default", "false", SettingValueType.Bool, SettingFlag.Public, 3, null),
        ("non_efs_zip_encoding", "", SettingValueType.String, SettingFlag.Public, 3, null),

        // --- Group 4: GLOBAL ---
        ("hide_files", "", SettingValueType.Text, SettingFlag.Public, 4, null),
        ("pagination_type", "all", SettingValueType.Select, SettingFlag.Public, 4, "all,pagination"),
        ("default_page_size", "30", SettingValueType.Number, SettingFlag.Public, 4, null),
        ("ignore_paths", "", SettingValueType.Text, SettingFlag.Public, 4, null),
        ("ignore_direct_link_params", "", SettingValueType.String, SettingFlag.Public, 4, null),
        ("forward_direct_link_params", "false", SettingValueType.Bool, SettingFlag.Public, 4, null),
        ("ignore_system_files", "false", SettingValueType.Bool, SettingFlag.Public, 4, null),
        ("filename_char_mapping", "", SettingValueType.Text, SettingFlag.Public, 4, null),
        ("privacy_regs", "", SettingValueType.Text, SettingFlag.Public, 4, null),
        ("handle_hook_after_writing", "false", SettingValueType.Bool, SettingFlag.Private, 4, null),
        ("handle_hook_rate_limit", "0", SettingValueType.Number, SettingFlag.Private, 4, null),
        ("sign_all", "false", SettingValueType.Bool, SettingFlag.Public, 4, null),
        ("link_expiration", "0", SettingValueType.Number, SettingFlag.Public, 4, null),
        ("proxy_ignore_headers", "", SettingValueType.String, SettingFlag.Private, 4, null),

        // --- Group 5: ARIA2 (offline download tools; shown on /settings/other) ---
        ("aria2_uri", "", SettingValueType.String, SettingFlag.Private, 5, null),
        ("aria2_secret", "", SettingValueType.String, SettingFlag.Private, 5, null),
        ("qbittorrent_url", "", SettingValueType.String, SettingFlag.Private, 5, null),
        ("qbittorrent_seedtime", "", SettingValueType.String, SettingFlag.Private, 5, null),
        ("transmission_uri", "", SettingValueType.String, SettingFlag.Private, 5, null),
        ("transmission_seedtime", "", SettingValueType.String, SettingFlag.Private, 5, null),

        // --- Group 6: INDEX ---
        ("search_index", "none", SettingValueType.Select, SettingFlag.Public, 6, "none,database,bleve"),
        ("auto_update_index", "false", SettingValueType.Bool, SettingFlag.Public, 6, null),
        ("max_index_depth", "20", SettingValueType.Number, SettingFlag.Public, 6, null),
        ("index_progress", "", SettingValueType.String, SettingFlag.Private, 6, null),

        // --- Group 7: SSO ---
        ("sso_login_enabled", "false", SettingValueType.Bool, SettingFlag.Public, 7, null),
        ("sso_login_platform", "Casdoor", SettingValueType.Select, SettingFlag.Public, 7, "Casdoor,Gitlab,Google,Microsoft,Discord,Twitter,Github"),
        ("sso_compatibility_mode", "false", SettingValueType.Bool, SettingFlag.Public, 7, null),
        ("sso_auto_register", "false", SettingValueType.Bool, SettingFlag.Public, 7, null),
        ("sso_default_dir", "/", SettingValueType.String, SettingFlag.Public, 7, null),
        ("sso_default_permission", "0", SettingValueType.Number, SettingFlag.Public, 7, null),
        ("sso_client_id", "", SettingValueType.String, SettingFlag.Private, 7, null),
        ("sso_client_secret", "", SettingValueType.String, SettingFlag.Private, 7, null),
        ("sso_endpoint_name", "", SettingValueType.String, SettingFlag.Private, 7, null),
        ("sso_organization_name", "", SettingValueType.String, SettingFlag.Private, 7, null),
        ("sso_application_name", "", SettingValueType.String, SettingFlag.Private, 7, null),
        ("sso_jwt_public_key", "", SettingValueType.Text, SettingFlag.Private, 7, null),
        ("sso_extra_scopes", "", SettingValueType.String, SettingFlag.Private, 7, null),
        ("sso_oidc_username_key", "", SettingValueType.String, SettingFlag.Private, 7, null),

        // --- Group 8: LDAP ---
        ("ldap_login_enabled", "false", SettingValueType.Bool, SettingFlag.Public, 8, null),
        ("ldap_login_tips", "", SettingValueType.String, SettingFlag.Public, 8, null),
        ("ldap_server", "", SettingValueType.String, SettingFlag.Private, 8, null),
        ("ldap_manager_dn", "", SettingValueType.String, SettingFlag.Private, 8, null),
        ("ldap_manager_password", "", SettingValueType.String, SettingFlag.Private, 8, null),
        ("ldap_user_search_base", "", SettingValueType.String, SettingFlag.Private, 8, null),
        ("ldap_user_search_filter", "", SettingValueType.String, SettingFlag.Private, 8, null),
        ("ldap_default_dir", "/", SettingValueType.String, SettingFlag.Public, 8, null),
        ("ldap_default_permission", "0", SettingValueType.Number, SettingFlag.Public, 8, null),
        ("ldap_skip_tls_verify", "false", SettingValueType.Bool, SettingFlag.Private, 8, null),

        // --- Group 9: S3 ---
        ("s3_buckets", "[]", SettingValueType.Text, SettingFlag.Private, 9, null),
        ("s3_access_key_id", "", SettingValueType.String, SettingFlag.Private, 9, null),
        ("s3_secret_access_key", "", SettingValueType.String, SettingFlag.Private, 9, null),

        // --- Group 10: FTP ---
        ("ftp_public_host", "", SettingValueType.String, SettingFlag.Private, 10, null),
        ("ftp_pasv_port_map", "", SettingValueType.String, SettingFlag.Private, 10, null),
        ("ftp_mandatory_tls", "false", SettingValueType.Bool, SettingFlag.Private, 10, null),
        ("ftp_implicit_tls", "false", SettingValueType.Bool, SettingFlag.Private, 10, null),
        ("ftp_tls_public_cert_path", "", SettingValueType.String, SettingFlag.Private, 10, null),
        ("ftp_tls_private_key_path", "", SettingValueType.String, SettingFlag.Private, 10, null),
        ("sftp_disable_password_login", "false", SettingValueType.Bool, SettingFlag.Private, 10, null),

        // --- Group 11: TRAFFIC ---
        // 速率单位为 KiB/s；<= 0 表示不限速（对齐 Go 的 streamFilterNegative：-1 为无限）。
        ("max_client_download_speed", "-1", SettingValueType.Number, SettingFlag.Public, 11, null),
        ("max_client_upload_speed", "-1", SettingValueType.Number, SettingFlag.Public, 11, null),
        ("max_server_download_speed", "-1", SettingValueType.Number, SettingFlag.Private, 11, null),
        ("max_server_upload_speed", "-1", SettingValueType.Number, SettingFlag.Private, 11, null),
        // Multipart 分片上传：multipart_chunk_size 单位为 MB；move_task_threads_num 对齐 Go 的 move_task_threads_num。
        ("multipart_enabled", "true", SettingValueType.Bool, SettingFlag.Public, 11, null),
        ("multipart_chunk_size", "10", SettingValueType.Number, SettingFlag.Public, 11, null),
        ("move_task_threads_num", "5", SettingValueType.Number, SettingFlag.Private, 11, null),
    };

    /// <summary>
    /// 装填前端所需的默认公开设置。SolidJS 前端通过 <c>GET /api/public/settings</c> 读取这些值，
    /// 并会调用类似 <c>.split('\n')</c> 的方法，因此每个键都必须存在（至少为空字符串），
    /// 否则会触发 <c>Cannot read properties of undefined</c> 错误。
    /// </summary>
    private static async Task SeedDefaultSettingsAsync(OpenListDbContext db, CancellationToken ct)
    {
        // 迁移旧版 group="ui" 到前端使用的数字分组，并纠正类型不匹配（如 announcement 由 String→Text）。
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

        foreach (var (key, value, type, flag, group, options) in DefaultSettings)
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
                Options = options,
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
