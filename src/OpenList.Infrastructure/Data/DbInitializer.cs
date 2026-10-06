using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    /// <param name="config">应用配置，用于读取数据库类型与管理员初始账号等。</param>
    /// <param name="devMode">开发模式（对应 Go 的 <c>--dev</c>）：用 "admin" 作为内置
    /// 管理员初始密码，免去本地每次都走安装向导。非开发模式且未配置
    /// <see cref="AppConfiguration.AdminPassword"/> 时不创建管理员。</param>
    /// <param name="ct">取消令牌（可选）。</param>
    /// <param name="logger">日志器（可选）。初始密码等启动凭据写到这里，
    /// 由调用方注入已配置好的 Serilog，从而落进日志文件；为 null 时退化为
    /// <see cref="Console"/> 输出（测试与库调用场景）。</param>
    /// <exception cref="Exception">任意数据库操作失败时抛出（由调用方决定是否放行）。</exception>
    public static async Task InitializeAsync(
        OpenListDbContext db, AppConfiguration config, bool devMode = false, CancellationToken ct = default,
        ILogger? logger = null)
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
        await SeedAdminAsync(db, config, devMode, ct, logger);
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
            // Built-in admin marker. Backfill: the earliest admin row of a pre-existing
            // database is the one SeedAdminAsync has been maintaining, so treat it as
            // built-in to keep it protected after the upgrade.
            ("x_users", "IsBuiltIn", "INTEGER NOT NULL DEFAULT 0", "BOOLEAN NOT NULL DEFAULT FALSE"),
            ("x_users", "BasePath", "TEXT NULL", "TEXT NULL"),
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

        // IsBuiltIn was just added as all-zero; without a backfill every pre-existing
        // admin would lose its delete protection after upgrade. Mark the earliest admin
        // row (the one the old SeedAdminAsync maintained) as built-in. Runs on both
        // providers and is idempotent.
        await BackfillBuiltInAdminAsync(db, ct);

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
    /// 回填 <c>IsBuiltIn</c> 标记：为已存在但没有该标记的旧库，把<em>最早创建的管理员</em>
    /// 标记为内置账号。旧版 <c>SeedAdminAsync</c> 每次启动都按配置校对同一条管理员记录，
    /// 因此"最早的管理员"就是它在维护的那条，升级后禁删保护得以延续。
    ///已有标记的库不受影响（<c>AnyAsync(IsBuiltIn)</c> 短路）。
    /// </summary>
    /// <remarks>
    /// 同时被 <see cref="MigrateSchemaAsync"/> 与集成测试调用，故为 internal 而非 private。
    /// </remarks>
    internal static async Task BackfillBuiltInAdminAsync(OpenListDbContext db, CancellationToken ct)
    {
        if (await db.Users.AnyAsync(u => u.IsBuiltIn, ct))
            return;

        var admin = await db.Users
            .Where(u => u.Role == UserRole.Admin)
            .OrderBy(u => u.Id)
            .FirstOrDefaultAsync(ct);
        if (admin is null)
            return; // 全新库，交给 SeedAdminAsync 创建。

        admin.IsBuiltIn = true;
        await db.SaveChangesAsync(ct);
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
    /// 创建内置账号，语义对齐 Go 版 <c>internal/bootstrap/data/user.go</c> 的
    /// <c>initUser()</c>（= <c>initAdmin</c> + <c>initGuest</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>管理员：仅在「没有管理员」且「能拿到初始密码」时才创建。</b>拿不到密码就不建 ——
    /// 对齐 Go 的 <c>initAdmin</c>：它在既无 <c>--dev</c> 又无 <c>OPENLIST_ADMIN_PASSWORD</c>
    /// 时直接 <c>return</c>，把建号交给 Web 安装向导
    /// <c>POST /api/public/init/setup</c>。这样部署后不会留下人人可猜的默认凭据。
    /// 用户装完向导后本方法即变成 no-op（管理员已存在），后续凭据改动只以数据库为准。
    /// </para>
    /// <para>
    /// <b>游客：只要没有就建</b>，与管理员判据相互独立。这点很关键 ——
    /// 若沿用旧的「用户表为空」判据，游客建好之后表永远不为空，管理员就再也建不出来。
    /// </para>
    /// </remarks>
    /// <param name="db">EF Core 数据库上下文。</param>
    /// <param name="config">应用配置，<see cref="AppConfiguration.AdminPassword"/> 为空时不建管理员。</param>
    /// <param name="devMode">开发模式（对应 Go 的 <c>--dev</c>）：用 "admin" 作为初始密码，
    /// 免去本地每次都要走一遍安装向导。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="logger">日志器（可选）；为 null 时凭据只打到控制台。</param>
    private static async Task SeedAdminAsync(
        OpenListDbContext db, AppConfiguration config, bool devMode, CancellationToken ct, ILogger? logger = null)
    {
        // 管理员：不存在才考虑创建。注意用 Role 判据而非"表为空"—— 游客会在同一次
        // 初始化里被创建，用表判据会让管理员永远建不出来。
        if (!await db.Users.AnyAsync(u => u.Role == UserRole.Admin, ct))
        {
            // 与 Go 一致：无初始密码 ⇒ 不自动建号，等 Web 安装向导。
            // devMode 优先，其次是配置里显式提供的密码（appsettings.json 或
            // OpenList__AdminPassword 环境变量）。
            var password = devMode
                ? "admin"
                : (string.IsNullOrWhiteSpace(config.AdminPassword) ? null : config.AdminPassword);

            if (password is null)
            {
                WriteCredential(logger, LogLevel.Warning,
                    "No admin account created: no initial password configured. " +
                    "Open the web UI to finish setup (POST /api/public/init/setup), " +
                    "or set AdminPassword (env: OpenList__AdminPassword) before the first start.");
            }
            else
            {
                var username = string.IsNullOrWhiteSpace(config.AdminUsername) ? "admin" : config.AdminUsername;
                var now = DateTimeOffset.UtcNow;
                db.Users.Add(new User
                {
                    Username = username,
                    PasswordHash = UserService.HashPassword(password),
                    Role = UserRole.Admin,
                    // 权限位对齐 Go:0x71FF（可见隐藏文件 ~ 可分享，含 WebDAV 读写与归档读取位）。
                    // 必须与 PublicEndpoints 的 /init/setup 建号路径保持一致，否则走安装向导
                    // 建的管理员权限会低于自动初始化的那个。
                    Permission = 0x71FF,
                    BasePath = "/",
                    Disabled = false,
                    IsBuiltIn = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });

                // 初始密码是这套凭据的唯一一次明文出现（库里只存哈希），必须让用户拿得到：
                // 走日志器，桌面端 GUI 场景下 stdout 不可见，只有日志文件/日志页能读到。
                WriteCredential(logger, LogLevel.Information,
                    $"Successfully created the built-in admin user \"{username}\" with the initial password: {password}");
            }
        }

        // 游客账号：Go 的 initGuest 固定创建（禁用、零权限、密码 "guest"），判据独立于管理员。
        // 本项目多数"匿名访问"路径用的是代码内构造的虚拟 Guest（Id=0），与此记录无关；
        // 建它是为了与 Go 版行为一致，并给管理界面一个可管理的游客账号。
        if (!await db.Users.AnyAsync(u => u.Username == "guest", ct))
        {
            var now = DateTimeOffset.UtcNow;
            db.Users.Add(new User
            {
                Username = "guest",
                PasswordHash = UserService.HashPassword("guest"),
                Role = UserRole.Guest,
                Permission = 0,
                BasePath = "/",
                Disabled = true,
                IsBuiltIn = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 输出启动凭据类消息。注入了 <see cref="ILogger"/> 就走日志（因而落进日志文件与
    /// 桌面端日志页），否则退回控制台 —— 库/测试直接调用时没有日志基础设施。
    /// </summary>
    /// <param name="logger">调用方注入的日志器，可为 null。</param>
    /// <param name="level">日志级别。</param>
    /// <param name="message">消息文本。</param>
    private static void WriteCredential(ILogger? logger, LogLevel level, string message)
    {
        if (logger is null)
        {
            Console.WriteLine(message);
            return;
        }
        logger.Log(level, "{CredentialMessage}", message);
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
