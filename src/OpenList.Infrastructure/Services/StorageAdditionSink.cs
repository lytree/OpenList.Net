using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Drivers;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// <see cref="IStorageAdditionSink"/> 的默认实现：把驱动回写的 Addition 落到
/// <c>x_storages</c> 表。对应 OpenList Go 的 <c>op.MustSaveDriverStorage</c>。
/// <para>
/// 注册为单例（与 <see cref="MountService"/> 生命周期一致），但 <c>OpenListDbContext</c>
/// 是 Scoped，因此这里通过 <see cref="IServiceScopeFactory"/> 为每次写入新建一个作用域 ——
/// 与 <c>RateLimitManager</c> / <c>OfflineDownloadService</c> 的既有做法一致。
/// </para>
/// </summary>
public sealed class StorageAdditionSink : IStorageAdditionSink
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;

    /// <summary>
    /// 构造回写通道。
    /// </summary>
    /// <param name="scopes">用于解析 Scoped 的 <c>OpenListDbContext</c>。</param>
    /// <param name="time">时间源，便于测试注入。</param>
    public StorageAdditionSink(IServiceScopeFactory scopes, TimeProvider? time = null)
    {
        _scopes = scopes;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<bool> SaveAdditionAsync(string mountPath, string addition, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mountPath)) return false;

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenListDbContext>();

        var storage = await db.Storages.FirstOrDefaultAsync(x => x.MountPath == mountPath, ct);
        if (storage is null) return false;

        // Addition 未变化时不写库，避免 token 刷新在无更新时产生无谓写放大。
        if (string.Equals(storage.Addition, addition, StringComparison.Ordinal)) return true;

        storage.Addition = addition;
        storage.Modified = _time.GetUtcNow();
        storage.UpdatedAt = storage.Modified;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
