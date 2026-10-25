using Microsoft.EntityFrameworkCore;
using OpenList.Core.Configuration;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.Data;

/// <summary>
/// EF Core 数据库上下文。表名前缀为 <c>x_</c>，支持 SQLite（默认）与 PostgreSQL。
/// 对应 Go 参考实现 <c>internal/db</c>。
/// </summary>
public sealed class OpenListDbContext : DbContext
{
    private readonly DatabaseConfig _config;

    /// <summary>
    /// 创建数据库上下文实例。
    /// </summary>
    /// <param name="options">EF Core 选项（含数据库提供程序配置）。</param>
    /// <param name="config">数据库配置，用于读取表前缀等。</param>
    public OpenListDbContext(DbContextOptions<OpenListDbContext> options, DatabaseConfig config) : base(options)
    {
        _config = config;
    }

    /// <summary>用户表。</summary>
    public DbSet<User> Users => Set<User>();
    /// <summary>存储（存储驱动实例）表。</summary>
    public DbSet<Storage> Storages => Set<Storage>();
    /// <summary>路径元数据表。</summary>
    public DbSet<Meta> Metas => Set<Meta>();
    /// <summary>分享记录表。</summary>
    public DbSet<Sharing> Sharings => Set<Sharing>();
    /// <summary>设置键值对表。</summary>
    public DbSet<SettingItem> Settings => Set<SettingItem>();
    /// <summary>SSH 公钥表。</summary>
    public DbSet<SshKey> SshKeys => Set<SshKey>();
    /// <summary>消息表（站内消息/广播）。</summary>
    public DbSet<Message> Messages => Set<Message>();
    /// <summary>搜索索引条目表。</summary>
    public DbSet<SearchIndexEntry> SearchIndex => Set<SearchIndexEntry>();
    /// <summary>WebAuthn 凭据表。</summary>
    public DbSet<WebAuthnCredential> AuthnCredentials => Set<WebAuthnCredential>();
    /// <summary>媒体库表。</summary>
    public DbSet<MediaLibrary> MediaLibraries => Set<MediaLibrary>();
    /// <summary>媒体库扫描历史表。</summary>
    public DbSet<MediaScanLog> MediaScanLogs => Set<MediaScanLog>();
    /// <summary>媒体条目表。</summary>
    public DbSet<MediaItem> MediaItems => Set<MediaItem>();
    /// <summary>媒体文件表。</summary>
    public DbSet<MediaFile> MediaFiles => Set<MediaFile>();
    /// <summary>创作者表。</summary>
    public DbSet<MediaPerson> MediaPersons => Set<MediaPerson>();
    /// <summary>媒体条目与创作者关联表。</summary>
    public DbSet<MediaItemPerson> MediaItemPersons => Set<MediaItemPerson>();
    /// <summary>用户媒体状态表。</summary>
    public DbSet<UserMediaState> UserMediaStates => Set<UserMediaState>();

    /// <summary>
    /// 配置实体到数据表的映射关系（表名、主键、唯一索引等）。
    /// 整个模型采用的是传统 TPH 之外的独立表映射，无导航属性。
    /// </summary>
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
        b.Entity<MediaLibrary>(e =>
        {
            e.ToTable(prefix + "media_libraries");
            e.HasKey(l => l.Id);
            e.HasIndex(l => l.Path).IsUnique();
        });
        b.Entity<MediaScanLog>(e =>
        {
            e.ToTable(prefix + "media_scan_logs");
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.LibraryId);
        });
        b.Entity<MediaItem>(e =>
        {
            e.ToTable(prefix + "media_items");
            e.HasKey(i => i.Id);
            e.HasIndex(i => i.LibraryId);
            e.HasIndex(i => new { i.LibraryId, i.DirectoryPath }).IsUnique();
            e.HasIndex(i => i.TmdbId);
        });
        b.Entity<MediaFile>(e =>
        {
            e.ToTable(prefix + "media_files");
            e.HasKey(f => f.Id);
            e.HasIndex(f => f.ItemId);
            e.HasIndex(f => f.Path).IsUnique();
        });
        b.Entity<MediaPerson>(e =>
        {
            e.ToTable(prefix + "media_persons");
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.Name);
            e.HasIndex(p => new { p.Name, p.TmdbId }).IsUnique();
        });
        b.Entity<MediaItemPerson>(e =>
        {
            e.ToTable(prefix + "media_item_persons");
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.ItemId);
            e.HasIndex(r => r.PersonId);
            e.HasIndex(r => new { r.ItemId, r.PersonId, r.Role }).IsUnique();
        });
        b.Entity<UserMediaState>(e =>
        {
            e.ToTable(prefix + "user_media_states");
            e.HasKey(s => s.Id);
            e.HasIndex(s => new { s.UserId, s.ItemId }).IsUnique();
        });
    }
}
