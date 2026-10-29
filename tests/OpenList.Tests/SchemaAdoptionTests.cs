using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Configuration;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Tests;

/// <summary>
/// 旧库接管（<see cref="LegacySchemaAdopter"/>）。
///
/// 这一组用例守的是一条**曾经断掉的路**：没有迁移历史的既有库上，<c>MigrateAsync</c>
/// 必然在 <c>Init</c> 撞表失败并被吞掉，于是它永远走不回迁移轨道 —— 之后每个迁移都不执行，
/// 每张新表都得在两份手写 DDL 里各写一遍。接管要保证：
/// <list type="number">
///   <item>旧库被登记成「全部迁移已应用」，从而下一个迁移能正常增量应用；</item>
///   <item>缺失的表按<em>模型</em>补出来，不是按人肉清单；</item>
///   <item>既有数据一行不动（只建表，不改表）；</item>
///   <item>重复启动不会重复盖章（历史表 <c>MigrationId</c> 是主键，重复插入会直接抛）。</item>
/// </list>
///
/// 每个用例用一次性**文件**库：内存库的 <c>Exists()</c> 恒为 true，会让 <c>EnsureCreated</c>
/// 与「库是否为空」的判断全部失真（见 <see cref="BuiltInAdminTests"/> 的同名注释）。
/// </summary>
public class SchemaAdoptionTests
{
    private const string SqliteMigrationsAssembly = "OpenList.Migrations.Sqlite";

    // ------------------------------------------------------------------
    // 全新库：仍走迁移，不该被接管逻辑插手
    // ------------------------------------------------------------------

    [Test]
    public async Task Fresh_Database_Ends_Up_With_Every_Migration_Applied()
    {
        var path = NewDbPath();
        await using var ctx = NewContext(path);

        await DbInitializer.InitializeAsync(ctx, new AppConfiguration());

        var known = ctx.Database.GetMigrations().ToList();
        // 迁移程序集必须真的被加载：否则下面「pending 为空」是平凡真（0 == 0）。
        await Assert.That(known.Count).IsGreaterThan(0);

        var applied = (await ctx.Database.GetAppliedMigrationsAsync()).ToList();
        await Assert.That(applied).IsEquivalentTo(known);
        await Assert.That(await ctx.Database.GetPendingMigrationsAsync()).IsEmpty();
    }

    // ------------------------------------------------------------------
    // 旧库：EnsureCreated 直建的库（无历史表）
    // ------------------------------------------------------------------

    [Test]
    public async Task Legacy_Database_Without_History_Is_Registered_As_Fully_Migrated()
    {
        var path = NewDbPath();
        await using (var seed = NewContext(path, withMigrations: false))
            await seed.Database.EnsureCreatedAsync();   // 早期版本：直建，没有 __EFMigrationsHistory

        await using var ctx = NewContext(path);
        await DbInitializer.InitializeAsync(ctx, new AppConfiguration());

        var known = ctx.Database.GetMigrations().ToList();
        var applied = (await ctx.Database.GetAppliedMigrationsAsync()).ToList();
        await Assert.That(applied).IsEquivalentTo(known);
        // 这才是目的：接管之后没有待应用迁移，下一个新迁移才会是 pending。
        await Assert.That(await ctx.Database.GetPendingMigrationsAsync()).IsEmpty();
    }

    /// <summary>
    /// 真实世界里的旧库往往<em>有</em>一张空的 <c>__EFMigrationsHistory</c>：
    /// EF 在应用迁移之前就把它建出来了，然后才在 <c>Init</c> 上撞表。
    /// 用「历史表是否存在」当判据会把这类库排除掉，所以判据必须是「有没有已应用的迁移」。
    /// </summary>
    [Test]
    public async Task Legacy_Database_With_An_Empty_History_Table_Is_Still_Adopted()
    {
        var path = NewDbPath();
        await using (var seed = NewContext(path, withMigrations: false))
        {
            await seed.Database.EnsureCreatedAsync();
            await seed.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                );
                """);
        }

        await using var ctx = NewContext(path);
        await DbInitializer.InitializeAsync(ctx, new AppConfiguration());

        await Assert.That((await ctx.Database.GetAppliedMigrationsAsync()).ToList())
            .IsEquivalentTo(ctx.Database.GetMigrations().ToList());
    }

    // ------------------------------------------------------------------
    // 缺失的表按模型补出来（Go 时代库：媒体域整块不存在）
    // ------------------------------------------------------------------

    [Test]
    public async Task Legacy_Database_Gets_Tables_That_Only_Exist_In_The_Model()
    {
        var path = NewDbPath();
        await using (var seed = NewContext(path, withMigrations: false))
        {
            await seed.Database.EnsureCreatedAsync();
            // 回到「媒体域还不存在」的年代：把媒体表整套删掉。
            foreach (var table in await ListTablesAsync(seed, "x_media", "x_user_media_states"))
                await seed.Database.ExecuteSqlRawAsync($"DROP TABLE \"{table}\";");
        }

        await using var ctx = NewContext(path);
        await Assert.That(await TableExistsAsync(ctx, "x_media_items")).IsFalse();

        await DbInitializer.InitializeAsync(ctx, new AppConfiguration());

        // 结论只看数据库本身：表在不在。
        foreach (var table in new[] { "x_media_items", "x_media_libraries", "x_media_playlists", "x_media_watch_sessions" })
            await Assert.That(await TableExistsAsync(ctx, table)).IsTrue();
    }

    /// <summary>
    /// 接管只建表、不改表，所以既有数据必须原样保留 —— 建表顺序写错（先删后建）也能让上面
    /// 的「表都在」用例通过，但用户数据会没。
    /// </summary>
    [Test]
    public async Task Adoption_Keeps_Existing_Rows()
    {
        var path = NewDbPath();
        await using (var seed = NewContext(path, withMigrations: false))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Users.Add(new User { Username = "keeper", Password = "", PasswordHash = "", Role = UserRole.General });
            await seed.SaveChangesAsync();
        }

        await using var ctx = NewContext(path);
        await DbInitializer.InitializeAsync(ctx, new AppConfiguration());

        await Assert.That(await ctx.Users.AnyAsync(u => u.Username == "keeper")).IsTrue();
    }

    /// <summary>
    /// 第二次启动不该再接管：<c>__EFMigrationsHistory.MigrationId</c> 是主键，
    /// 重复盖章会直接抛主键冲突（而不是静默多一行），所以这条也顺带守住了幂等实现。
    /// </summary>
    [Test]
    public async Task Adoption_Is_Idempotent_Across_Restarts()
    {
        var path = NewDbPath();
        await using (var seed = NewContext(path, withMigrations: false))
            await seed.Database.EnsureCreatedAsync();

        await using (var first = NewContext(path))
            await DbInitializer.InitializeAsync(first, new AppConfiguration());

        var known = 0;
        await using (var second = NewContext(path))
        {
            await DbInitializer.InitializeAsync(second, new AppConfiguration());
            var applied = (await second.Database.GetAppliedMigrationsAsync()).ToList();
            known = second.Database.GetMigrations().Count();
            await Assert.That(applied).HasCount().EqualTo(known);
        }
    }

    // ------------------------------------------------------------------
    // 工具
    // ------------------------------------------------------------------

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), $"openlist-adopt-{Guid.NewGuid():N}.db");

    /// <summary>
    /// <paramref name="withMigrations"/> = false 用于<em>造假旧库</em>：不挂迁移程序集，
    /// <c>EnsureCreated</c> 就只按模型直建，正好复刻早期版本的行为。
    /// </summary>
    private static OpenListDbContext NewContext(string path, bool withMigrations = true)
    {
        var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        var builder = new DbContextOptionsBuilder<OpenListDbContext>();
        if (withMigrations)
            builder.UseSqlite(conn, o => o.MigrationsAssembly(SqliteMigrationsAssembly));
        else
            builder.UseSqlite(conn);
        return new OpenListDbContext(builder.Options, new DatabaseConfig());
    }

    /// <summary>读出以指定前缀开头的表（外加显式列出的表名）。</summary>
    private static async Task<List<string>> ListTablesAsync(OpenListDbContext ctx, string prefix, params string[] exact)
    {
        var conn = ctx.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(0);
            if (name.StartsWith(prefix, StringComparison.Ordinal) || exact.Contains(name))
                names.Add(name);
        }
        return names;
    }

    private static async Task<bool> TableExistsAsync(OpenListDbContext ctx, string table)
    {
        var conn = ctx.Database.GetDbConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@t;";
        var p = cmd.CreateParameter();
        p.ParameterName = "@t";
        p.Value = table;
        cmd.Parameters.Add(p);
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync();
    }
}
