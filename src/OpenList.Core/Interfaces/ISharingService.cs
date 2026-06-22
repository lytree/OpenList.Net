using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

public interface ISharingService
{
    Task<Resp<List<Sharing>>> ListAsync(long userId, CancellationToken ct = default);
    Task<Resp<Sharing>> GetAsync(long id, CancellationToken ct = default);
    Task<Resp<Sharing>> CreateAsync(CreateSharingReq req, long userId, CancellationToken ct = default);
    Task<Resp> UpdateAsync(long id, UpdateSharingReq req, CancellationToken ct = default);
    Task<Resp> DeleteAsync(long id, CancellationToken ct = default);
    Task<Resp> EnableAsync(long id, CancellationToken ct = default);
    Task<Resp> DisableAsync(long id, CancellationToken ct = default);
    Task<Resp<Sharing>> VerifyAsync(long id, string? password, CancellationToken ct = default);
}

public sealed class CreateSharingReq
{
    public string Path { get; set; } = string.Empty;
    public string? Password { get; set; }
    public long? Expiration { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class UpdateSharingReq
{
    public string? Path { get; set; }
    public string? Password { get; set; }
    public long? Expiration { get; set; }
    public bool? Enabled { get; set; }
}
