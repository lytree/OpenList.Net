using OpenList.Core.Drivers;
using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

public interface ISharingService
{
    Task<(List<Sharing> Sharings, int Total)> ListSharingsAsync(uint userId, int page = 1, int perPage = 100, CancellationToken ct = default);
    Task<Sharing?> GetSharingAsync(string id, CancellationToken ct = default);
    Task<Sharing> CreateSharingAsync(Sharing sharing, CancellationToken ct = default);
    Task UpdateSharingAsync(Sharing sharing, CancellationToken ct = default);
    Task UpdateSharingIdAsync(string oldId, string newId, CancellationToken ct = default);
    Task DeleteSharingAsync(string id, CancellationToken ct = default);
    Task EnableSharingAsync(string id, CancellationToken ct = default);
    Task DisableSharingAsync(string id, CancellationToken ct = default);
    Task<(Sharing? sharing, List<IObj> objs)> ListSharingObjectsAsync(string sid, string path, string? pwd = null, bool refresh = false, CancellationToken ct = default);
    Task<(Sharing? sharing, IObj? obj)> GetSharingObjectAsync(string sid, string path, string? pwd = null, CancellationToken ct = default);
    Task IncrementAccessAsync(string id, string ip, CancellationToken ct = default);
}
