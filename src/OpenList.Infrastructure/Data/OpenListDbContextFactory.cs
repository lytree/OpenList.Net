using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OpenList.Core.Configuration;

namespace OpenList.Infrastructure.Data;

/// <summary>
/// 用于 EF Core 迁移的设计时工厂。通过环境变量 <c>OPENLIST_DB_PROVIDER</c> 选择数据库提供程序
/// （默认 <c>sqlite</c>，<c>postgres</c> 使用 Npgsql），以使每个提供程序拥有各自的迁移集：
/// <list type="bullet">
/// <item><c>OPENLIST_DB_PROVIDER=sqlite dotnet ef migrations add Init --project src/OpenList.Migrations.Sqlite --startup-project src/OpenList.Server</c></item>
/// <item><c>OPENLIST_DB_PROVIDER=postgres dotnet ef migrations add Init --project src/OpenList.Migrations.Postgres --startup-project src/OpenList.Server</c></item>
/// </list>
/// EF Core 10 移除 <c>MigrationsNamespace</c>，官方的多提供程序做法是为每个提供程序使用独立的
/// 迁移程序集。传给 <see cref="SqliteDbContextOptionsBuilder.MigrationsAssembly(string)"/> /
/// <see cref="NpgsqlDbContextOptionsBuilder.MigrationsAssembly(string)"/> 的程序集名必须与命令行
/// <c>--project</c> 一致。
/// </summary>
public sealed class OpenListDbContextFactory : IDesignTimeDbContextFactory<OpenListDbContext>
{
    /// <summary>
    /// 创建一个设计时使用的数据库上下文。根据 <c>OPENLIST_DB_PROVIDER</c> 选择 SQLite 或
    /// PostgreSQL，并指定对应的迁移程序集。
    /// </summary>
    /// <param name="args">命令行参数（此实现忽略）。</param>
    /// <returns>配置好的 <see cref="OpenListDbContext"/> 实例。</returns>
    public OpenListDbContext CreateDbContext(string[] args)
    {
        var provider = Environment.GetEnvironmentVariable("OPENLIST_DB_PROVIDER") ?? "sqlite";
        var config = new DatabaseConfig();
        var opts = new DbContextOptionsBuilder<OpenListDbContext>();

        if (provider.Equals("postgres", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("postgresql", StringComparison.OrdinalIgnoreCase))
        {
            opts.UseNpgsql(
                "Host=localhost;Database=openlist;Username=openlist;Password=openlist",
                npgsql => npgsql.MigrationsAssembly("OpenList.Migrations.Postgres"));
        }
        else
        {
            opts.UseSqlite(
                "Data Source=data/data.db",
                sqlite => sqlite.MigrationsAssembly("OpenList.Migrations.Sqlite"));
        }

        return new OpenListDbContext(opts.Options, config);
    }
}
