using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OpenList.Core.Configuration;

namespace OpenList.Infrastructure.Data;

/// <summary>
/// Design-time factory for EF migrations. Provider is selected via the
/// <c>OPENLIST_DB_PROVIDER</c> environment variable (<c>sqlite</c> default,
/// <c>postgres</c> for Npgsql) so each provider gets its own migration set:
/// <list type="bullet">
/// <item><c>OPENLIST_DB_PROVIDER=sqlite dotnet ef migrations add Init --project src/OpenList.Migrations.Sqlite --startup-project src/OpenList.Server</c></item>
/// <item><c>OPENLIST_DB_PROVIDER=postgres dotnet ef migrations add Init --project src/OpenList.Migrations.Postgres --startup-project src/OpenList.Server</c></item>
/// </list>
/// EF Core 10 dropped <c>MigrationsNamespace</c>; the official multi-provider
/// approach is one migration assembly per provider. The assembly name passed
/// to <see cref="SqliteDbContextOptionsBuilder.MigrationsAssembly(string)"/> /
/// <see cref="NpgsqlDbContextOptionsBuilder.MigrationsAssembly(string)"/> must
/// match the <c>--project</c> used on the command line.
/// </summary>
public sealed class OpenListDbContextFactory : IDesignTimeDbContextFactory<OpenListDbContext>
{
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
