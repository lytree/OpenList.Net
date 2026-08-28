using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// 路径元数据管理服务接口。
/// </summary>
public interface IMetaService
{
    /// <summary>获取全部元数据。</summary>
    Task<Resp<List<Meta>>> ListAsync(CancellationToken ct = default);
    /// <summary>按路径获取元数据。</summary>
    Task<Resp<Meta>> GetAsync(string path, CancellationToken ct = default);
    /// <summary>按 id 获取元数据。</summary>
    Task<Resp<Meta>> GetByIdAsync(long id, CancellationToken ct = default);
    /// <summary>创建元数据。</summary>
    Task<Resp<Meta>> CreateAsync(Meta meta, CancellationToken ct = default);
    /// <summary>更新指定元数据。</summary>
    Task<Resp> UpdateAsync(long id, Meta meta, CancellationToken ct = default);
    /// <summary>删除指定元数据。</summary>
    Task<Resp> DeleteAsync(long id, CancellationToken ct = default);
}
