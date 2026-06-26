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
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<SearchIndexEntry> SearchIndex => Set<SearchIndexEntry>();
    public DbSet<WebAuthnCredential> AuthnCredentials => Set<WebAuthnCredential>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        var prefix = _config.TablePrefix;
        b.Entity<User>(e =>
        {
            e.ToTable(prefix + "users");
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.Username).IsUnique();
        });
        b.Entity<Storage>(e =>
        {
            e.ToTable(prefix + "storages");
            e.HasKey(s => s.Id);
        });
        b.Entity<Meta>(e =>
        {
            e.ToTable(prefix + "metas");
            e.HasKey(m => m.Id);
            e.HasIndex(m => m.Path).IsUnique();
        });
        b.Entity<Sharing>(e =>
        {
            e.ToTable(prefix + "sharings");
            e.HasKey(s => s.Id);
        });
        b.Entity<SettingItem>(e =>
        {
            e.ToTable(prefix + "settings");
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(256);
        });
        b.Entity<SshKey>(e =>
        {
            e.ToTable(prefix + "sshkeys");
            e.HasKey(k => k.Id);
            e.HasIndex(k => k.UserId);
        });
        b.Entity<Message>(e =>
        {
            e.ToTable(prefix + "messages");
            e.HasKey(m => m.Id);
            e.HasIndex(m => m.UserId);
        });
        b.Entity<SearchIndexEntry>(e =>
        {
            e.ToTable(prefix + "search_index");
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.Name);
            e.HasIndex(s => s.Parent);
            e.HasIndex(s => s.Path).IsUnique();
        });
        b.Entity<WebAuthnCredential>(e =>
        {
            e.ToTable(prefix + "authn_credentials");
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.UserId);
            // Credential ids must be unique per user so re-registering a key
            // with the same authenticator replaces rather than duplicates.
            e.HasIndex(c => new { c.UserId, c.CredentialId }).IsUnique();
        });
    }
}
