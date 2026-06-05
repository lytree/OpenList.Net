using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Configuration;

namespace OpenList.Infrastructure.Data;

public static class DbContextExtensions
{
    public static IServiceCollection AddOpenListDbContext(this IServiceCollection services, DatabaseConfig dbConfig)
    {
        services.AddDbContext<OpenListDbContext>(options =>
        {
            switch (dbConfig.Type.ToLower())
            {
                case "sqlite3":
                case "sqlite":
                    options.UseSqlite($"Data Source={dbConfig.DBFile}");
                    break;
                case "mysql":
                    var mysqlConn = $"Server={dbConfig.Host};Port={dbConfig.Port};Database={dbConfig.Name};User={dbConfig.User};Password={dbConfig.Password};";
                    options.UseMySql(mysqlConn, ServerVersion.AutoDetect(mysqlConn));
                    break;
                case "postgres":
                case "postgresql":
                    var pgConn = $"Host={dbConfig.Host};Port={dbConfig.Port};Database={dbConfig.Name};Username={dbConfig.User};Password={dbConfig.Password};SSL Mode={dbConfig.SSLMode}";
                    options.UseNpgsql(pgConn);
                    break;
                case "mssql":
                case "sqlserver":
                    var mssqlConn = $"Server={dbConfig.Host},{dbConfig.Port};Database={dbConfig.Name};User Id={dbConfig.User};Password={dbConfig.Password};TrustServerCertificate=True;";
                    options.UseSqlServer(mssqlConn);
                    break;
                default:
                    options.UseSqlite($"Data Source={dbConfig.DBFile}");
                    break;
            }
        });
        return services;
    }

    public static async Task InitializeDatabaseAsync(this IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();
        await dbContext.Database.EnsureCreatedAsync();
    }
}
