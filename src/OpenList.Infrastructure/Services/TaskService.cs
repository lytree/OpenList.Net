using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;

namespace OpenList.Infrastructure.Services;

public class TaskService : ITaskService
{
    private readonly OpenListDbContext _db;
    private readonly ConcurrentDictionary<string, TaskInfo> _runningTasks = new();

    public TaskService(OpenListDbContext db) => _db = db;

    public async Task<TaskInfo> CreateTaskAsync(string type, string description, CancellationToken ct = default)
    {
        var task = new TaskInfo { Type = type, Description = description, State = (int)TaskState.Pending };
        var record = TaskRecord.FromTaskInfo(task);
        _db.TaskRecords.Add(record);
        await _db.SaveChangesAsync(ct);
        return task;
    }

    public async Task<TaskInfo> CreateTaskAsync(string type, string description, Func<string, CancellationToken, Task> execute, CancellationToken ct = default)
    {
        var task = new TaskInfo { Type = type, Description = description, State = (int)TaskState.Running };
        var record = TaskRecord.FromTaskInfo(task);
        _db.TaskRecords.Add(record);
        await _db.SaveChangesAsync(ct);
        _runningTasks[task.Id] = task;

        _ = Task.Run(async () =>
        {
            try
            {
                await execute(task.Id, ct);
                task.State = (int)TaskState.Succeeded;
                task.Progress = 100;
            }
            catch (OperationCanceledException)
            {
                task.State = (int)TaskState.Canceled;
            }
            catch (Exception ex)
            {
                task.State = (int)TaskState.Errored;
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
            task.State = (int)TaskState.Canceled;

        var record = await _db.TaskRecords.FindAsync([id], ct);
        if (record != null)
        {
            record.State = (int)TaskState.Canceled;
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task RetryTaskAsync(string id, CancellationToken ct = default)
    {
        if (_runningTasks.TryGetValue(id, out var task))
        {
            task.State = (int)TaskState.Pending;
            task.Progress = 0;
            task.Error = null;
        }

        var record = await _db.TaskRecords.FindAsync([id], ct);
        if (record != null)
        {
            record.State = (int)TaskState.Pending;
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

    private static readonly HashSet<int> UndoneStates = [(int)TaskState.Pending, (int)TaskState.Running, (int)TaskState.Canceling, (int)TaskState.Errored, (int)TaskState.Failing, (int)TaskState.WaitingRetry, (int)TaskState.BeforeRetry];
    private static readonly HashSet<int> DoneStates = [(int)TaskState.Canceled, (int)TaskState.Failed, (int)TaskState.Succeeded];

    public async Task<List<TaskInfo>> ListUndoneTasksAsync(string? type = null, CancellationToken ct = default)
    {
        var all = await ListTasksAsync(type, ct);
        return all.Where(t => UndoneStates.Contains(t.State)).ToList();
    }

    public async Task<List<TaskInfo>> ListDoneTasksAsync(string? type = null, CancellationToken ct = default)
    {
        var all = await ListTasksAsync(type, ct);
        return all.Where(t => DoneStates.Contains(t.State)).ToList();
    }

    public async Task CancelSomeAsync(List<string> ids, CancellationToken ct = default)
    {
        foreach (var id in ids)
            await CancelTaskAsync(id, ct);
    }

    public async Task DeleteSomeAsync(List<string> ids, CancellationToken ct = default)
    {
        foreach (var id in ids)
            await DeleteTaskAsync(id, ct);
    }

    public async Task RetrySomeAsync(List<string> ids, CancellationToken ct = default)
    {
        foreach (var id in ids)
            await RetryTaskAsync(id, ct);
    }

    public async Task ClearDoneAsync(uint? userId = null, string? type = null, CancellationToken ct = default)
    {
        var done = await ListDoneTasksAsync(type, ct);
        // Go: only clear tasks belonging to the user (or all if admin/null)
        if (userId.HasValue)
            done = done.Where(t => t.CreatorId == userId.Value).ToList();
        foreach (var task in done)
            await DeleteTaskAsync(task.Id, ct);
    }

    public async Task ClearSucceededAsync(uint? userId = null, string? type = null, CancellationToken ct = default)
    {
        var query = _db.TaskRecords.AsQueryable();
        if (!string.IsNullOrEmpty(type))
            query = query.Where(t => t.Type == type);
        if (userId.HasValue)
            query = query.Where(t => t.CreatorId == userId.Value);
        var succeeded = await query.Where(t => t.State == (int)TaskState.Succeeded).ToListAsync(ct);
        _db.TaskRecords.RemoveRange(succeeded);
        await _db.SaveChangesAsync(ct);
    }

    public async Task RetryFailedAsync(uint? userId = null, string? type = null, CancellationToken ct = default)
    {
        var query = _db.TaskRecords.AsQueryable();
        if (!string.IsNullOrEmpty(type))
            query = query.Where(t => t.Type == type);
        if (userId.HasValue)
            query = query.Where(t => t.CreatorId == userId.Value);
        var failed = await query.Where(t => t.State == (int)TaskState.Errored || t.State == (int)TaskState.Failed).ToListAsync(ct);
        foreach (var record in failed)
        {
            record.State = (int)TaskState.Pending;
            record.Progress = 0;
            record.Error = null;
        }
        await _db.SaveChangesAsync(ct);
    }

    public static string GenerateNanoId(int size)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-";
        var bytes = new byte[size];
        RandomNumberGenerator.Fill(bytes);
        return new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
    }
}

