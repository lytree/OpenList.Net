using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

public enum TaskType
{
    Copy,
    Upload,
    Transfer,
    Download,
    Decompress,
    DecompressUpload,
    Move,
}

public interface ITaskService
{
    Task<Resp<TaskInfo>> InfoAsync(TaskType type, string tid, CancellationToken ct = default);
    Task<Resp<List<TaskInfo>>> DoneAsync(TaskType type, CancellationToken ct = default);
    Task<Resp<List<TaskInfo>>> UndoneAsync(TaskType type, CancellationToken ct = default);
    Task<Resp> DeleteAsync(TaskType type, string tid, CancellationToken ct = default);
    Task<Resp> CancelAsync(TaskType type, string tid, CancellationToken ct = default);
    Task<Resp> ClearAsync(TaskType type, CancellationToken ct = default);
    Task<Resp> RetryAsync(TaskType type, string tid, CancellationToken ct = default);
    Task<string> CreateAsync(TaskType type, Func<IProgress<TaskProgress>, CancellationToken, Task> work, CancellationToken ct = default);
}

public sealed class TaskInfo
{
    public string Id { get; set; } = string.Empty;
    public TaskType Type { get; set; }
    public string Name { get; set; } = string.Empty;
    public string State { get; set; } = "pending";
    public string? Error { get; set; }
    public long Progress { get; set; }
    public long Total { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class TaskProgress
{
    public long Current { get; set; }
    public long Total { get; set; }
}
