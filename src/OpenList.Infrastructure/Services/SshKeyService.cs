using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// SSH 公钥的增删查服务。从公钥数据中计算标准的 <c>SHA256:base64</c> 指纹。
/// 对应 Go <c>internal/op/sshkey.go</c>。
/// </summary>
public sealed class SshKeyService : ISshKeyService
{
    private readonly OpenListDbContext _db;
    private readonly TimeProvider _time;

    /// <summary>
    /// 创建 SSH 公钥服务实例。
    /// </summary>
    /// <param name="db">EF Core 数据库上下文。</param>
    /// <param name="time">时间源（可选，默认系统时区）。</param>
    public SshKeyService(OpenListDbContext db, TimeProvider? time = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// 列出指定用户的公钥，按 Id 倒序返回。
    /// </summary>
    public async Task<Resp<List<SshKeyInfo>>> ListAsync(long userId, CancellationToken ct = default)
    {
        var keys = await _db.SshKeys.AsNoTracking()
            .Where(k => k.UserId == userId)
            .OrderByDescending(k => k.Id)
            .ToListAsync(ct);
        return Resp<List<SshKeyInfo>>.Ok(keys.Select(ToInfo).ToList());
    }

    /// <summary>
    /// 为用户添加一条公钥：校验标题与密钥合法性、计算指纹后入库。
    /// </summary>
    /// <param name="userId">用户 Id。</param>
    /// <param name="title">密钥标题。</param>
    /// <param name="key">OpenSSH 公钥文本。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回公钥信息；无效标题/密钥返回 400。</returns>
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

    /// <summary>
    /// 删除公钥并返回受影响用户的剩余密钥列表（供前端更新）。
    /// 非管理员只能删除自己的密钥，管理员可删除任意用户的密钥。
    /// </summary>
    /// <param name="userId">请求用户 Id。</param>
    /// <param name="keyId">待删除的密钥 Id。</param>
    /// <param name="isAdmin">是否为管理员。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<Resp<List<SshKeyInfo>>> DeleteAsync(long userId, long keyId, bool isAdmin, CancellationToken ct = default)
    {
        var entity = await _db.SshKeys.FirstOrDefaultAsync(k => k.Id == keyId, ct);
        if (entity is null)
            return Resp<List<SshKeyInfo>>.Fail("ssh key not found", RespCodes.NotFound);
        // 非管理员仅能删除自己的密钥；管理员可删除任意用户的密钥。
        if (!isAdmin && entity.UserId != userId)
            return Resp<List<SshKeyInfo>>.Fail("permission denied", RespCodes.Forbidden);

        var targetUserId = entity.UserId;
        _db.SshKeys.Remove(entity);
        await _db.SaveChangesAsync(ct);

        // 返回受影响用户的剩余密钥（前端据此更新列表）。
        var remaining = await _db.SshKeys.AsNoTracking()
            .Where(k => k.UserId == targetUserId)
            .OrderByDescending(k => k.Id)
            .ToListAsync(ct);
        return Resp<List<SshKeyInfo>>.Ok(remaining.Select(ToInfo).ToList());
    }

    /// <summary>
    /// 将实体转换为对外信息 DTO（Id 转为字符串并格式化时间）。
    /// </summary>
    private static SshKeyInfo ToInfo(SshKey k) => new()
    {
        Id = k.Id.ToString(),
        Title = k.Name,
        Fingerprint = k.Fingerprint,
        AddedTime = k.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        LastUsedTime = k.LastUsedTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
    };

    /// <summary>
    /// 计算 SSH 公钥指纹，格式为 <c>SHA256:&lt;base64&gt;</c>。
    /// 接受标准 OpenSSH 格式：<c>ssh-rsa AAAA... [comment]</c> 或仅 base64 数据。
    /// 无法解析时返回 null。
    /// </summary>
    private static string? ComputeFingerprint(string key)
    {
        var parts = key.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;

        // parts[0] = 密钥类型（ssh-rsa、ssh-ed25519、ecdsa-...），parts[1] = base64 数据
        var blobBase64 = parts[1];
        byte[] blob;
        try { blob = Convert.FromBase64String(blobBase64); }
        catch { return null; }

        var hash = SHA256.HashData(blob);
        return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
    }
}
