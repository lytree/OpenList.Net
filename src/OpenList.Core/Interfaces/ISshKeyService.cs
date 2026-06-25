using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// SSH public-key management for the current user and (admin) any user.
/// Mirrors Go <c>server/handles/me.go</c> sshkey routes and
/// <c>server/handles/admin/user.go</c> admin sshkey routes.
/// </summary>
public interface ISshKeyService
{
    Task<Resp<List<SshKeyInfo>>> ListAsync(long userId, CancellationToken ct = default);
    Task<Resp<SshKeyInfo>> AddAsync(long userId, string title, string key, CancellationToken ct = default);
    Task<Resp<List<SshKeyInfo>>> DeleteAsync(long userId, long keyId, bool isAdmin, CancellationToken ct = default);
}

/// <summary>
/// DTO matching the frontend <c>SSHPublicKey</c> interface (types/sshkey.ts).
/// Field names use snake_case to match the frontend's expected JSON.
/// </summary>
public sealed class SshKeyInfo
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public string AddedTime { get; set; } = string.Empty;
    public string LastUsedTime { get; set; } = string.Empty;
}
