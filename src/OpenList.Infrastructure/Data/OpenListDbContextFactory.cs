using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OpenList.Core.Configuration;

namespace OpenList.Infrastructure.Data;

public class OpenListDbContextFactory : IDesignTimeDbContextFactory<OpenListDbContext>
{
    public OpenListDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<OpenListDbContext>();

        return new OpenListDbContext(optionsBuilder.Options);
    }
}
