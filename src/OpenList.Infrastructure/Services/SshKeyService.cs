using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// SSH public-key CRUD. Computes the standard <c>SHA256:base64</c> fingerprint
/// from the key blob. Mirrors Go <c>internal/op/sshkey.go</c>.
/// </summary>
public sealed class SshKeyService : ISshKeyService
{
    private readonly OpenListDbContext _db;
    private readonly TimeProvider _time;

    public SshKeyService(OpenListDbContext db, TimeProvider? time = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
    }

    public async Task<Resp<List<SshKeyInfo>>> ListAsync(long userId, CancellationToken ct = default)
    {
        var keys = await _db.SshKeys.AsNoTracking()
            .Where(k => k.UserId == userId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);
        return Resp<List<SshKeyInfo>>.Ok(keys.Select(ToInfo).ToList());
    }

    public async Task<Resp<SshKeyInfo>> AddAsync(long userId, string title, string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Resp<SshKeyInfo>.Fail("title is required", RespCodes.BadRequest);
        if (string.IsNullOrWhiteSpace(key))
            return Resp<SshKeyInfo>.Fail("key is required", RespCodes.BadRequest);

        var fingerprint = ComputeFingerprint(key);
        if (fingerprint is null)
            return Resp<SshKeyInfo>.Fail("invalid ssh public key", RespCodes.BadRequest);

        var now = _time.GetUtcNow();
        var entity = new SshKey
        {
            UserId = userId,
            Name = title,
            PublicKey = key.Trim(),
            Fingerprint = fingerprint,
            CreatedAt = now,
            LastUsedTime = now,
        };
        _db.SshKeys.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Resp<SshKeyInfo>.Ok(ToInfo(entity));
    }

    public async Task<Resp<List<SshKeyInfo>>> DeleteAsync(long userId, long keyId, bool isAdmin, CancellationToken ct = default)
    {
        var entity = await _db.SshKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct);
        if (entity is null)
            return Resp<List<SshKeyInfo>>.Fail("ssh key not found", RespCodes.NotFound);
        // Non-admins can only delete their own keys; admins can delete any user's key.
        if (!isAdmin && entity.UserId != userId)
            return Resp<List<SshKeyInfo>>.Fail("permission denied", RespCodes.Forbidden);

        var targetUserId = entity.UserId;
        _db.SshKeys.Remove(entity);
        await _db.SaveChangesAsync(ct);

        // Return the remaining keys for the affected user (frontend updates its list).
        var remaining = await _db.SshKeys.AsNoTracking()
            .Where(k => k.UserId == targetUserId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);
        return Resp<List<SshKeyInfo>>.Ok(remaining.Select(ToInfo).ToList());
    }

    private static SshKeyInfo ToInfo(SshKey k) => new()
    {
        Id = k.Id.ToString(),
        Title = k.Name,
        Fingerprint = k.Fingerprint,
        AddedTime = k.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        LastUsedTime = k.LastUsedTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
    };

    /// <summary>
    /// Computes the SSH public key fingerprint as <c>SHA256:&lt;base64&gt;</c>.
    /// Accepts the standard OpenSSH format: <c>ssh-rsa AAAA... [comment]</c> or
    /// just the base64 blob. Returns null if the key cannot be parsed.
    /// </summary>
    private static string? ComputeFingerprint(string key)
    {
        var parts = key.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;

        // parts[0] = key type (ssh-rsa, ssh-ed25519, ecdsa-...), parts[1] = base64 blob
        var blobBase64 = parts[1];
        byte[] blob;
        try { blob = Convert.FromBase64String(blobBase64); }
        catch { return null; }

        var hash = SHA256.HashData(blob);
        return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
    }
}
