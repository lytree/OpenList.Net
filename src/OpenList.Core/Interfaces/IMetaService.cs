using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

public interface IMetaService
{
    Task<(List<Meta> Metas, int Total)> ListMetasAsync(int page = 1, int perPage = 100, CancellationToken ct = default);
    Task<Meta?> GetMetaAsync(string path, CancellationToken ct = default);
    Task<Meta?> GetMetaByIdAsync(uint id, CancellationToken ct = default);
    Task<Meta?> GetNearestMetaAsync(string path, CancellationToken ct = default);
    Task CreateMetaAsync(Meta meta, CancellationToken ct = default);
    Task UpdateMetaAsync(Meta meta, CancellationToken ct = default);
    Task DeleteMetaAsync(string path, CancellationToken ct = default);
    Task DeleteMetaByIdAsync(uint id, CancellationToken ct = default);
}
