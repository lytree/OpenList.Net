using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

public class SignService : ISignService
{
    private readonly byte[] _key;
    private readonly int _linkExpirationHours;

    public SignService(IConfiguration config)
    {
        var secret = config["SignKey"] ?? "openlist-sign-key";
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        _linkExpirationHours = config.GetValue("LinkExpiration", 0); // 0 = never expires
    }

    public string Sign(string path)
    {
        var expire = _linkExpirationHours > 0
            ? DateTimeOffset.UtcNow.AddHours(_linkExpirationHours).ToUnixTimeSeconds()
            : 0L;

        return ComputeSign(path, expire);
    }

    public string SignArchive(string path) => Sign("archive:" + path);

    public bool Verify(string sign, string path)
    {
        if (string.IsNullOrEmpty(sign)) return false;
        try
        {
            // Format: base64url(hmac):expireTimestamp
            var colonIndex = sign.LastIndexOf(':');
            if (colonIndex < 0) return false;

            var expireStr = sign[(colonIndex + 1)..];
            if (!long.TryParse(expireStr, out var expire)) return false;

            // Check expiration (0 = never expires)
            if (expire != 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expire) return false;

            // Verify signature
            var expected = ComputeSign(path, expire);
            return sign == expected;
        }
        catch { return false; }
    }

    public bool VerifyArchive(string sign, string path) => Verify(sign, "archive:" + path);

    private string ComputeSign(string data, long expire)
    {
        var payload = $"{data}:{expire}";
        using var hmac = new HMACSHA256(_key);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_').TrimEnd('=') + ":" + expire;
    }
}

public class SharingService : ISharingService
{
    private readonly OpenListDbContext _db;

    public SharingService(OpenListDbContext db) => _db = db;

    public async Task<List<Sharing>> ListSharingsAsync(uint userId, int page = 1, int perPage = 100, CancellationToken ct = default)
    {
        var query = userId == 0
            ? _db.Sharings.AsQueryable()
            : _db.Sharings.Where(s => s.CreatorId == userId);
        return await query.OrderByDescending(s => s.Id)
            .Skip((page - 1) * perPage).Take(perPage).ToListAsync(ct);
    }

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
    private readonly OpenListDbContext _db;
    private readonly ConcurrentDictionary<string, TaskInfo> _runningTasks = new();

    public TaskService(OpenListDbContext db) => _db = db;

    public async Task<TaskInfo> CreateTaskAsync(string type, string description, CancellationToken ct = default)
    {
        var task = new TaskInfo { Type = type, Description = description, State = "pending" };
        var record = TaskRecord.FromTaskInfo(task);
        _db.TaskRecords.Add(record);
        await _db.SaveChangesAsync(ct);
        return task;
    }

    public async Task<TaskInfo> CreateTaskAsync(string type, string description, Func<string, CancellationToken, Task> execute, CancellationToken ct = default)
    {
        var task = new TaskInfo { Type = type, Description = description, State = "running" };
        var record = TaskRecord.FromTaskInfo(task);
        _db.TaskRecords.Add(record);
        await _db.SaveChangesAsync(ct);
        _runningTasks[task.Id] = task;

        _ = Task.Run(async () =>
        {
            try
            {
                await execute(task.Id, ct);
                task.State = "succeeded";
                task.Progress = 100;
            }
            catch (OperationCanceledException)
            {
                task.State = "canceled";
            }
            catch (Exception ex)
            {
                task.State = "errored";
                task.Error = ex.Message;
            }
            finally
            {
                _runningTasks.TryRemove(task.Id, out _);
                // Persist final state
                var dbRecord = await _db.TaskRecords.FindAsync([task.Id], ct);
                if (dbRecord != null)
                {
                    dbRecord.State = task.State;
                    dbRecord.Progress = task.Progress;
                    dbRecord.Error = task.Error;
                    try { await _db.SaveChangesAsync(ct); } catch { /* best effort */ }
                }
            }
        }, ct);

        return task;
    }

    public async Task<List<TaskInfo>> ListTasksAsync(string? type = null, CancellationToken ct = default)
    {
        var query = _db.TaskRecords.AsQueryable();
        if (!string.IsNullOrEmpty(type))
            query = query.Where(t => t.Type == type);

        var records = await query.OrderByDescending(t => t.CreatedAt).ToListAsync(ct);
        var result = records.Select(r => r.ToTaskInfo()).ToList();

        // Merge running tasks (in-memory state is more up-to-date)
        foreach (var running in _runningTasks.Values)
        {
            var idx = result.FindIndex(t => t.Id == running.Id);
            if (idx >= 0) result[idx] = running;
        }

        return result;
    }

    public async Task<TaskInfo?> GetTaskAsync(string id, CancellationToken ct = default)
    {
        // Check running tasks first (more up-to-date)
        if (_runningTasks.TryGetValue(id, out var running))
            return running;

        var record = await _db.TaskRecords.FindAsync([id], ct);
        return record?.ToTaskInfo();
    }

    public async Task CancelTaskAsync(string id, CancellationToken ct = default)
    {
        if (_runningTasks.TryGetValue(id, out var task))
            task.State = "canceled";

        var record = await _db.TaskRecords.FindAsync([id], ct);
        if (record != null)
        {
            record.State = "canceled";
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task RetryTaskAsync(string id, CancellationToken ct = default)
    {
        if (_runningTasks.TryGetValue(id, out var task))
        {
            task.State = "pending";
            task.Progress = 0;
            task.Error = null;
        }

        var record = await _db.TaskRecords.FindAsync([id], ct);
        if (record != null)
        {
            record.State = "pending";
            record.Progress = 0;
            record.Error = null;
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task DeleteTaskAsync(string id, CancellationToken ct = default)
    {
        _runningTasks.TryRemove(id, out _);
        var record = await _db.TaskRecords.FindAsync([id], ct);
        if (record != null)
        {
            _db.TaskRecords.Remove(record);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task UpdateProgressAsync(string id, double progress, CancellationToken ct = default)
    {
        if (_runningTasks.TryGetValue(id, out var info))
        {
            info.Progress = progress;
        }
        var record = await _db.TaskRecords.FindAsync([id], ct);
        if (record != null)
        {
            record.Progress = progress;
            await _db.SaveChangesAsync(ct);
        }
    }

    public static string GenerateNanoId(int size)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-";
        var bytes = new byte[size];
        RandomNumberGenerator.Fill(bytes);
        return new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
    }
}
