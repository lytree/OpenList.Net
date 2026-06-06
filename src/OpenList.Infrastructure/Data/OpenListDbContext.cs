using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.Data;

public class OpenListDbContext : DbContext
{
    public OpenListDbContext(DbContextOptions<OpenListDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Storage> Storages => Set<Storage>();
    public DbSet<Meta> Metas => Set<Meta>();
    public DbSet<SettingItem> Settings => Set<SettingItem>();
    public DbSet<Sharing> Sharings => Set<Sharing>();
    public DbSet<SshKey> SshKeys => Set<SshKey>();
    public DbSet<TaskRecord> TaskRecords => Set<TaskRecord>();
    public DbSet<SearchIndex> SearchIndices => Set<SearchIndex>();
    public DbSet<MessageRecord> Messages => Set<MessageRecord>();
    public DbSet<TokenBlacklistEntry> TokenBlacklist => Set<TokenBlacklistEntry>();
    public DbSet<WebAuthnCredential> WebAuthnCredentials => Set<WebAuthnCredential>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // User
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Username).IsUnique();
            entity.Property(e => e.PwdHash).HasColumnName("pwd_hash");
            entity.Property(e => e.PwdTS).HasColumnName("pwd_ts");
            entity.Property(e => e.BasePath).HasDefaultValue("/");
            entity.Property(e => e.Role).HasDefaultValue(UserRole.General);
            entity.Property(e => e.AllowLdap).HasDefaultValue(true);
            entity.Ignore(e => e.IsGuest);
            entity.Ignore(e => e.IsAdmin);
        });

        // Storage
        modelBuilder.Entity<Storage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.MountPath).IsUnique();
            entity.Property(e => e.Addition).HasColumnType("text");
            entity.Property(e => e.CustomCachePolicies).HasColumnType("text");
            entity.Property(e => e.Status).HasDefaultValue("");
            entity.Property(e => e.WebdavPolicy).HasDefaultValue("native_proxy");
        });

        // Meta
        modelBuilder.Entity<Meta>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Path).IsUnique();
            var uintListConverter = new ValueConverter<List<uint>, string>(
                v => string.Join(',', v),
                v => string.IsNullOrEmpty(v) ? new List<uint>() : v.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(uint.Parse).ToList());
            entity.Property(e => e.ReadUsers).HasConversion(uintListConverter);
            entity.Property(e => e.WriteUsers).HasConversion(uintListConverter);
        });

        // SettingItem
        modelBuilder.Entity<SettingItem>(entity =>
        {
            entity.HasKey(e => e.Key);
        });

        // Sharing
        modelBuilder.Entity<Sharing>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(64);
            entity.Property(e => e.FilesRaw).HasColumnType("text");
            entity.Property(e => e.Readme).HasColumnType("text");
            entity.Property(e => e.Header).HasColumnType("text");
            entity.Ignore(e => e.Files);
            entity.Ignore(e => e.Creator);
        });

        // SshKey
        modelBuilder.Entity<SshKey>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // TaskRecord
        modelBuilder.Entity<TaskRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Type);
            entity.HasIndex(e => e.State);
            entity.HasIndex(e => e.CreatedAt);
            entity.Property(e => e.Error).HasColumnType("text");
            entity.Property(e => e.PersistData).HasColumnType("text");
        });

        // SearchIndex
        modelBuilder.Entity<SearchIndex>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Parent);
            entity.HasIndex(e => e.Name);
            entity.Property(e => e.Parent).IsRequired();
            entity.Property(e => e.Name).IsRequired();
        });

        // MessageRecord
        modelBuilder.Entity<MessageRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.Read);
        });

        // TokenBlacklistEntry
        modelBuilder.Entity<TokenBlacklistEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Jti);
            entity.HasIndex(e => e.ExpiresAt);
        });

        // WebAuthnCredential
        modelBuilder.Entity<WebAuthnCredential>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.CredentialId);
            entity.Property(e => e.PublicKey).HasColumnType("text");
            entity.Property(e => e.Transports).HasColumnType("text");
        });
    }
}
