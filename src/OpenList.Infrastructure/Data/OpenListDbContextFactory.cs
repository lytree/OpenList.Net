using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OpenList.Core.Configuration;

namespace OpenList.Infrastructure.Data;

/// <summary>
/// Design-time factory for EF migrations (uses SQLite by convention).
/// Run: <c>dotnet ef migrations add Init --project src/OpenList.Infrastructure</c>
/// </summary>
public sealed class OpenListDbContextFactory : IDesignTimeDbContextFactory<OpenListDbContext>
{
    public OpenListDbContext CreateDbContext(string[] args)
    {
        var opts = new DbContextOptionsBuilder<OpenListDbContext>()
            .UseSqlite("Data Source=data/data.db")
            .Options;
        return new OpenListDbContext(opts, new DatabaseConfig());
    }
}
