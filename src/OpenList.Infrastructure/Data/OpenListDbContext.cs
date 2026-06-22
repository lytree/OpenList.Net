using Microsoft.EntityFrameworkCore;
using OpenList.Core.Configuration;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.Data;

/// <summary>
/// EF Core DbContext. Table prefix: <c>x_</c>. Supports SQLite (default) and PostgreSQL.
/// Mirrors OpenList Go <c>internal/db</c>.
/// </summary>
public sealed class OpenListDbContext : DbContext
{
    private readonly DatabaseConfig _config;

    public OpenListDbContext(DbContextOptions<OpenListDbContext> options, DatabaseConfig config) : base(options)
    {
        _config = config;
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Storage> Storages => Set<Storage>();
    public DbSet<Meta> Metas => Set<Meta>();
    public DbSet<Sharing> Sharings => Set<Sharing>();
    public DbSet<SettingItem> Settings => Set<SettingItem>();
    public DbSet<SshKey> SshKeys => Set<SshKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        var prefix = _config.TablePrefix;
        b.Entity<User>().ToTable(prefix + "users");
        b.Entity<Storage>().ToTable(prefix + "storages");
        b.Entity<Meta>().ToTable(prefix + "metas");
        b.Entity<Sharing>().ToTable(prefix + "sharings");
        b.Entity<SettingItem>().ToTable(prefix + "settings");
        b.Entity<SshKey>().ToTable(prefix + "sshkeys");
    }
}
