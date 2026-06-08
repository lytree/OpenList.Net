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
            var pgConn = $"Host={dbConfig.Host};Port={dbConfig.Port};Database={dbConfig.Name};Username={dbConfig.User};Password={dbConfig.Password};SSL Mode={dbConfig.SSLMode}";
            options.UseNpgsql(pgConn);
        });
        return services;
    }

    public static async Task InitializeDatabaseAsync(this IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();

        // Use MigrateAsync for proper migration support.
        // If no migrations exist yet, this will create the database with the current schema.
        // To create the initial migration, run:
        //   dotnet ef migrations add InitialCreate --project src/OpenList.Infrastructure --startup-project src/OpenList.Server
        if ((await dbContext.Database.GetPendingMigrationsAsync()).Any())
        {
            await dbContext.Database.MigrateAsync();
        }
        else
        {
            // Fallback for databases without migrations history table
            await dbContext.Database.EnsureCreatedAsync();
        }
    }
}
