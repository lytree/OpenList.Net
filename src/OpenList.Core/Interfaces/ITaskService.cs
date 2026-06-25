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
    OfflineDownload,
    OfflineDownloadTransfer,
    Aria2Down,
    Aria2Transfer,
    QbitDown,
    QbitTransfer,
}

public interface ITaskService
{
    Task<Resp<TaskInfo>> InfoAsync(TaskType type, string tid, CancellationToken ct = default);
    Task<Resp<List<TaskInfo>>> DoneAsync(TaskType type, CancellationToken ct = default);
    Task<Resp<List<TaskInfo>>> UndoneAsync(TaskType type, CancellationToken ct = default);
    Task<Resp> DeleteAsync(TaskType type, string tid, CancellationToken ct = default);
    Task<Resp> CancelAsync(TaskType type, string tid, CancellationToken ct = default);
    Task<Resp> ClearAsync(TaskType type, CancellationToken ct = default);
    Task<Resp> ClearSucceededAsync(TaskType type, CancellationToken ct = default);
    Task<Resp> RetryAsync(TaskType type, string tid, CancellationToken ct = default);
    Task<Resp> RetryFailedAsync(TaskType type, CancellationToken ct = default);
    /// <summary>Batch cancel; returns per-tid errors (key=tid, value=error message).</summary>
    Task<Resp<Dictionary<string, string>>> CancelSomeAsync(TaskType type, IReadOnlyList<string> tids, CancellationToken ct = default);
    /// <summary>Batch delete; returns per-tid errors.</summary>
    Task<Resp<Dictionary<string, string>>> DeleteSomeAsync(TaskType type, IReadOnlyList<string> tids, CancellationToken ct = default);
    /// <summary>Batch retry; returns per-tid errors.</summary>
    Task<Resp<Dictionary<string, string>>> RetrySomeAsync(TaskType type, IReadOnlyList<string> tids, CancellationToken ct = default);
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
