using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace OpenList.Infrastructure.Data;

/// <summary>
/// 把「一个迁移都没应用过的既有库」接管到 EF Core 迁移轨道上。
///
/// <para>
/// 背景（<c>docs/unified-media-product-plan.md</c> §44）：C# 版本早期的库由
/// <see cref="DatabaseFacade.EnsureCreatedAsync"/> 直建（Go 时代遗留库同理）。这类库上
/// <see cref="DatabaseFacade.MigrateAsync"/> 必然失败——第一个迁移 <c>Init</c> 就要
/// CREATE TABLE 已经存在的表——于是每次启动都被 <c>catch</c> 吞掉退回 <c>EnsureCreated</c>。
/// 结果是<em>它永远走不回迁移轨道</em>：之后每个迁移都不会执行，每张新表都得在
/// <see cref="DbInitializer"/> 里手写两遍 DDL（SQLite + PostgreSQL）。
/// </para>
///
/// <para>
/// 更要紧的是失败本身会留下痕迹：EF 在应用迁移**之前**就建出 <c>__EFMigrationsHistory</c>，
/// 所以旧库通常"有历史表、但一行没有"。判断是否已接管必须看行数，不能看表是否存在。
/// </para>
///
/// <para>
/// 接管分两步，都是幂等的：
/// </para>
/// <list type="number">
///   <item>
///     用 <see cref="IMigrationsModelDiffer"/> 拿「空模型 → 当前模型」的差异，只取
///     <see cref="CreateTableOperation"/> 里**库里还没有的**表建出来。之所以用模型而不是手写 DDL，
///     是因为模型是唯一的真相源——迁移文件只是它在某一时刻的快照，手写 DDL 必然与模型漂移
///     （历史上就漏过 <c>x_user_media_states</c> 的三列）。
///   </item>
///   <item>
///     把<em>全部</em>迁移 id 写进 <c>__EFMigrationsHistory</c>：此刻库的结构已经等于当前模型，
///     再从头跑一遍迁移只会撞表。盖章之后，下一个新迁移就能正常增量应用——这才是本次改动的目的。
///   </item>
/// </list>
///
/// <para>
/// 只管「整张表缺席」；已有表缺列仍由 <see cref="DbInitializer.MigrateSchemaAsync"/> 的补列清单负责
/// （那里的列声明是逐列斟酌过的，NOT NULL 默认值等语义不能靠类型映射猜）。
/// 失败一律让调用方吞掉并退回旧路径——接管是优化，不是启动的必要条件。
/// </para>
/// </summary>
internal static class LegacySchemaAdopter
{
    /// <summary>
    /// 尝试接管既有库。
    /// </summary>
    /// <returns>
    /// <c>true</c> = 已接管（库现在等价于「全部迁移已应用」）；
    /// <c>false</c> = 无需接管（空库，或本来就由迁移管理）。
    /// </returns>
    /// <exception cref="Exception">建表或盖章失败时抛出，由调用方决定是否退回 EnsureCreated。</exception>
    public static async Task<bool> TryAdoptAsync(
        OpenListDbContext db, bool isPostgres, ILogger? logger, CancellationToken ct = default)
    {
        // 空库：交给 MigrateAsync（正常路径），没有可接管的东西。
        // 注意这里用 HasTablesAsync 而不是「表数 > 0」——它会走 provider 自己的实现。
        var creator = db.Database.GetService<IRelationalDatabaseCreator>();
        if (!await creator.HasTablesAsync(ct))
            return false;

        // 判据是「一个迁移都没应用过」，不是「历史表不存在」。
        // 旧库上第一次失败启动时，EF 会**先建出空的 __EFMigrationsHistory**，再去跑 Init
        // （撞表失败）。于是真实世界里的旧库往往有这么一张空表 —— 用「表是否存在」判断，
        // 正好会把最需要接管的库排除掉。
        var applied = await db.Database.GetAppliedMigrationsAsync(ct);
        if (applied.Any())
            return false;

        var designModel = db.GetService<IDesignTimeModel>().Model;
        var relational = designModel.GetRelationalModel();

        // 空模型 → 当前模型 = 「这个库应该长什么样」。里面既有 CreateTable 也有 AddColumn/索引等；
        // 我们只取 CreateTable —— 已有表的列由 MigrateSchemaAsync 管，见类型注释。
        var createTables = db.GetService<IMigrationsModelDiffer>()
            .GetDifferences(null, relational)
            .OfType<CreateTableOperation>()
            .ToList();

        var missing = new List<MigrationOperation>();
        foreach (var op in createTables)
        {
            // Name 在 EF 的标注里可空；空名不可能是合法表，跳过而不是让后面报错。
            if (string.IsNullOrEmpty(op.Name))
                continue;
            if (await DbInitializer.TableExistsAsync(db, op.Name, isPostgres, ct))
                continue;
            missing.Add(op);
        }

        if (missing.Count > 0)
        {
            var generator = db.GetService<IMigrationsSqlGenerator>();
            foreach (var command in generator.Generate(missing, designModel))
                await db.Database.ExecuteSqlRawAsync(command.CommandText, ct);
        }

        // 盖章：先确保历史表存在，再逐个写入。
        var history = db.GetService<IHistoryRepository>();
        await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), ct);

        var version = ProductInfo.GetVersion();
        var stamped = 0;
        foreach (var migrationId in db.Database.GetMigrations())
        {
            await db.Database.ExecuteSqlRawAsync(
                history.GetInsertScript(new HistoryRow(migrationId, version)), ct);
            stamped++;
        }

        logger?.LogInformation(
            "旧库（无迁移历史）已接管：补建 {Tables} 张表，登记 {Migrations} 个迁移。" +
            "此后新增迁移可正常增量应用。",
            missing.Count, stamped);
        return true;
    }
}
