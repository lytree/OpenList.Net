using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

public class SignService : ISignService
{
    private static readonly byte[] _key = SHA256.HashData(Encoding.UTF8.GetBytes("openlist-sign-key"));

    public string Sign(string path)
    {
        var data = Encoding.UTF8.GetBytes(path);
        using var hmac = new HMACSHA256(_key);
        var hash = hmac.ComputeHash(data);
        var expiry = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
        var expiryBytes = BitConverter.GetBytes(expiry);
        var combined = hash.Concat(expiryBytes).ToArray();
        return Convert.ToHexString(combined).ToLowerInvariant();
    }

    public string SignArchive(string path) => Sign("archive:" + path);

    public bool Verify(string sign, string path)
    {
        if (string.IsNullOrEmpty(sign)) return false;
        try
        {
            var bytes = Convert.FromHexString(sign);
            if (bytes.Length < 40) return false;
            var expiry = BitConverter.ToInt64(bytes, 32);
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiry) return false;
            var data = Encoding.UTF8.GetBytes(path);
            using var hmac = new HMACSHA256(_key);
            var expectedHash = hmac.ComputeHash(data);
            return bytes[..32].SequenceEqual(expectedHash);
        }
        catch { return false; }
    }

    public bool VerifyArchive(string sign, string path) => Verify(sign, "archive:" + path);
}

public class SharingService : ISharingService
{
    private readonly OpenListDbContext _db;

    public SharingService(OpenListDbContext db) => _db = db;

    public async Task<List<Sharing>> ListSharingsAsync(uint userId, CancellationToken ct = default)
        => await _db.Sharings.Where(s => s.CreatorId == userId).ToListAsync(ct);

    public async Task<Sharing?> GetSharingAsync(string id, CancellationToken ct = default)
        => await _db.Sharings.FindAsync([id], ct);

    public async Task<Sharing> CreateSharingAsync(Sharing sharing, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sharing.Id))
            sharing.Id = TaskService.GenerateNanoId(8);
        _db.Sharings.Add(sharing);
        await _db.SaveChangesAsync(ct);
        return sharing;
    }

    public async Task UpdateSharingAsync(Sharing sharing, CancellationToken ct = default)
    {
        _db.Sharings.Update(sharing);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteSharingAsync(string id, CancellationToken ct = default)
    {
        var sharing = await _db.Sharings.FindAsync([id], ct);
        if (sharing != null)
        {
            _db.Sharings.Remove(sharing);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task EnableSharingAsync(string id, CancellationToken ct = default)
    {
        var sharing = await _db.Sharings.FindAsync([id], ct);
        if (sharing != null)
        {
            sharing.Disabled = false;
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task DisableSharingAsync(string id, CancellationToken ct = default)
    {
        var sharing = await _db.Sharings.FindAsync([id], ct);
        if (sharing != null)
        {
            sharing.Disabled = true;
            await _db.SaveChangesAsync(ct);
        }
    }
}

public class TaskService : ITaskService
{
    private readonly ConcurrentDictionary<string, TaskInfo> _tasks = new();

    public Task<TaskInfo> CreateTaskAsync(string type, string description, CancellationToken ct = default)
    {
        var task = new TaskInfo { Type = type, Description = description, State = "pending" };
        _tasks[task.Id] = task;
        return Task.FromResult(task);
    }

    public Task<List<TaskInfo>> ListTasksAsync(string? type = null, CancellationToken ct = default)
    {
        var tasks = _tasks.Values.AsEnumerable();
        if (!string.IsNullOrEmpty(type))
            tasks = tasks.Where(t => t.Type == type);
        return Task.FromResult(tasks.OrderByDescending(t => t.CreatedAt).ToList());
    }

    public Task<TaskInfo?> GetTaskAsync(string id, CancellationToken ct = default)
        => Task.FromResult(_tasks.GetValueOrDefault(id));

    public Task CancelTaskAsync(string id, CancellationToken ct = default)
    {
        if (_tasks.TryGetValue(id, out var task))
            task.State = "canceled";
        return Task.CompletedTask;
    }

    public Task RetryTaskAsync(string id, CancellationToken ct = default)
    {
        if (_tasks.TryGetValue(id, out var task))
        {
            task.State = "pending";
            task.Progress = 0;
            task.Error = null;
        }
        return Task.CompletedTask;
    }

    public Task DeleteTaskAsync(string id, CancellationToken ct = default)
    {
        _tasks.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public static string GenerateNanoId(int size)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-";
        var bytes = new byte[size];
        RandomNumberGenerator.Fill(bytes);
        return new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
    }
}
