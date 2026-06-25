using System.Text.Json.Serialization;
using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// Sharing management. Mirrors OpenList Go <c>internal/op/sharing.go</c> +
/// <c>server/handles/sharing.go</c>. Management lookups use the user-visible
/// <see cref="Sharing.ShareCode"/> (frontend string id), not the db primary key.
/// </summary>
public interface ISharingService
{
    Task<Resp<List<ShareInfo>>> ListAsync(long userId, bool isAdmin, CancellationToken ct = default);
    Task<Resp<ShareInfo>> GetAsync(string code, long userId, bool isAdmin, CancellationToken ct = default);
    Task<Resp<ShareInfo>> CreateAsync(ShareReq req, long userId, bool isAdmin, CancellationToken ct = default);
    Task<Resp<ShareInfo>> UpdateAsync(string code, ShareReq req, long userId, bool isAdmin, CancellationToken ct = default);
    Task<Resp> DeleteAsync(string code, long userId, bool isAdmin, CancellationToken ct = default);
    Task<Resp> EnableAsync(string code, long userId, bool isAdmin, CancellationToken ct = default);
    Task<Resp> DisableAsync(string code, long userId, bool isAdmin, CancellationToken ct = default);
    Task<Resp<Sharing>> VerifyAsync(string code, string? password, CancellationToken ct = default);
}

/// <summary>Request DTO for create/update — mirrors Go UpdateSharingReq.</summary>
public sealed class ShareReq
{
    /// <summary>User-visible share id (Go: string id). Empty on create → server generates.</summary>
    [JsonPropertyName("id")]
    public string? ShareCode { get; set; }

    [JsonPropertyName("new_id")]
    public string? NewShareCode { get; set; }

    public List<string> Files { get; set; } = [];

    [JsonPropertyName("expires")]
    public DateTimeOffset? Expiration { get; set; }

    [JsonPropertyName("pwd")]
    public string? Password { get; set; }

    [JsonPropertyName("max_accessed")]
    public int MaxAccessed { get; set; }

    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; }

    [JsonPropertyName("remark")]
    public string? Remark { get; set; }

    [JsonPropertyName("readme")]
    public string? Readme { get; set; }

    [JsonPropertyName("header")]
    public string? Header { get; set; }

    [JsonPropertyName("order_by")]
    public string? OrderBy { get; set; }

    [JsonPropertyName("order_direction")]
    public string? OrderDirection { get; set; }

    [JsonPropertyName("extract_folder")]
    public string? ExtractFolder { get; set; }

    [JsonPropertyName("creator")]
    public string? CreatorName { get; set; }

    [JsonPropertyName("accessed")]
    public int Accessed { get; set; }
}

/// <summary>Response DTO — mirrors Go SharingResp.</summary>
public sealed class ShareInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    public List<string> Files { get; set; } = [];

    [JsonPropertyName("expires")]
    public DateTimeOffset? Expires { get; set; }

    [JsonPropertyName("pwd")]
    public string? Pwd { get; set; }

    [JsonPropertyName("max_accessed")]
    public int MaxAccessed { get; set; }

    [JsonPropertyName("accessed")]
    public int Accessed { get; set; }

    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; }

    [JsonPropertyName("remark")]
    public string? Remark { get; set; }

    [JsonPropertyName("readme")]
    public string? Readme { get; set; }

    [JsonPropertyName("header")]
    public string? Header { get; set; }

    [JsonPropertyName("order_by")]
    public string? OrderBy { get; set; }

    [JsonPropertyName("order_direction")]
    public string? OrderDirection { get; set; }

    [JsonPropertyName("extract_folder")]
    public string? ExtractFolder { get; set; }

    [JsonPropertyName("creator")]
    public string Creator { get; set; } = string.Empty;

    [JsonPropertyName("creator_role")]
    public int CreatorRole { get; set; }
}
