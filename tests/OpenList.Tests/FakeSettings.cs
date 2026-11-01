using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Tests;

/// <summary>
/// 只读设置表的假实现：给需要
/// <see cref="ThumbnailOptionsProvider"/> / 其它"把设置项读成快照"的组件喂固定值。
/// <para>
/// 原来每个测试类各抄一份（缩略图三处），加一个接口成员就要改三遍 —— 收到这里。
/// 写操作一律返回成功，因为被测组件只读不写。
/// </para>
/// </summary>
internal sealed class FakeSettings(Dictionary<string, string> values) : ISettingService
{
    public Task<Resp<List<SettingItem>>> ListAsync(CancellationToken ct = default)
        => Task.FromResult(Resp<List<SettingItem>>.Ok(
            [.. values.Select(kv => new SettingItem { Key = kv.Key, Value = kv.Value })]));

    public Task<Resp<SettingItem>> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(Resp<SettingItem>.Fail("not found", RespCodes.NotFound));

    public Task<Resp> SaveAsync(List<SettingItem> items, CancellationToken ct = default)
        => Task.FromResult(Resp.Ok());

    public Task<Resp> DeleteAsync(string key, CancellationToken ct = default)
        => Task.FromResult(Resp.Ok());

    public Task<Resp<List<SettingItem>>> DefaultAsync(string? group = null, CancellationToken ct = default)
        => Task.FromResult(Resp<List<SettingItem>>.Ok([]));

    public Task<Resp> ResetTokenAsync(CancellationToken ct = default)
        => Task.FromResult(Resp.Ok());

    public Task<Resp<Dictionary<string, string>>> GetPublicAsync(CancellationToken ct = default)
        => Task.FromResult(Resp<Dictionary<string, string>>.Ok([]));

    public Task<Resp<string[]>> GetOfflineDownloadToolsAsync(CancellationToken ct = default)
        => Task.FromResult(Resp<string[]>.Ok([]));

    public Task<Resp<string[]>> GetArchiveExtensionsAsync(CancellationToken ct = default)
        => Task.FromResult(Resp<string[]>.Ok([]));
}
