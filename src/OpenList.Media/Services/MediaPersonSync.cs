using Microsoft.EntityFrameworkCore;
using OpenList.Infrastructure.Data;
using OpenList.Media.Domain;

namespace OpenList.Media.Services;

/// <summary>
/// 演职员落库助手：按 (Name, TmdbId) 去重写入 <see cref="MediaPerson"/>，并重建
/// 指定条目的 <see cref="MediaItemPerson"/> 关联。
/// <para>
/// 从 <see cref="MediaMetadataService"/> 抽出的原因：NFO 双向化后，扫描期解析本地
/// NFO 也要写演职员（TmdbId 为空），与刮削回写共用同一套去重与关联逻辑。
/// </para>
/// </summary>
internal static class MediaPersonSync
{
    /// <summary>一次待写入的演职员信息。</summary>
    /// <param name="Name">姓名（空白的会被跳过）。</param>
    /// <param name="TmdbId">provider 人物 id，本地 NFO 来源为 null。</param>
    /// <param name="Role">角色类型：director / actor / creator。</param>
    /// <param name="Character">演员饰演的角色名。</param>
    /// <param name="ProfilePath">头像路径。</param>
    /// <param name="Order">演员表排序。</param>
    public sealed record Entry(
        string Name, string? TmdbId, string Role, string? Character, string? ProfilePath, int Order);

    /// <summary>清除条目原有关联后按给定名单重建。顺序：<paramref name="persons"/> 的枚举顺序。</summary>
    public static async Task ReplaceAsync(
        OpenListDbContext db, TimeProvider time, long itemId,
        IEnumerable<Entry> persons, CancellationToken ct)
    {
        var existingLinks = await db.MediaItemPersons.Where(r => r.ItemId == itemId).ToListAsync(ct);
        db.MediaItemPersons.RemoveRange(existingLinks);
        await db.SaveChangesAsync(ct);

        foreach (var p in persons.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
        {
            var person = await db.MediaPersons.FirstOrDefaultAsync(
                x => x.Name == p.Name && x.TmdbId == p.TmdbId, ct);
            if (person is null)
            {
                person = new MediaPerson
                {
                    Name = p.Name,
                    TmdbId = p.TmdbId,
                    ProfilePath = p.ProfilePath,
                    CreatedAt = time.GetUtcNow(),
                    UpdatedAt = time.GetUtcNow(),
                };
                db.MediaPersons.Add(person);
                await db.SaveChangesAsync(ct);
            }
            db.MediaItemPersons.Add(new MediaItemPerson
            {
                ItemId = itemId,
                PersonId = person.Id,
                Role = p.Role,
                Character = p.Character,
                Order = p.Order,
            });
        }
        await db.SaveChangesAsync(ct);
    }
}
