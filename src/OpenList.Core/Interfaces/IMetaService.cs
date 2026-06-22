using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

public interface IMetaService
{
    Task<Resp<List<Meta>>> ListAsync(CancellationToken ct = default);
    Task<Resp<Meta>> GetAsync(string path, CancellationToken ct = default);
    Task<Resp<Meta>> CreateAsync(Meta meta, CancellationToken ct = default);
    Task<Resp> UpdateAsync(long id, Meta meta, CancellationToken ct = default);
    Task<Resp> DeleteAsync(long id, CancellationToken ct = default);
}
