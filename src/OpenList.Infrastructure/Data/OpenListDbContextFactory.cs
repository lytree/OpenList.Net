using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpenList.Infrastructure.Data;

public class OpenListDbContextFactory : IDesignTimeDbContextFactory<OpenListDbContext>
{
    public OpenListDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<OpenListDbContext>();
        optionsBuilder.UseSqlite("Data Source=data/data.db");
        return new OpenListDbContext(optionsBuilder.Options);
    }
}
