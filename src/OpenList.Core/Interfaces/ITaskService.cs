using System.Text.Json.Serialization;
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

/// <summary>
/// Task lifecycle state. Mirrors OpenList Go <c>pkg/task.TaskState</c>:
/// the numeric value is what the frontend reads as <c>state</c>.
/// </summary>
public enum TaskState : int
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Errored = 3,
    Canceling = 4,
    Canceled = 5,
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

    /// <summary>Numeric state consumed by the frontend (see <see cref="TaskState"/>).</summary>
    public TaskState State { get; set; } = TaskState.Pending;

    /// <summary>Human-readable status text shown in the UI (e.g. "pending", "running").</summary>
    public string Status => State.ToString().ToLowerInvariant();

    [JsonPropertyName("progress")]
    public long Progress { get; set; }

    [JsonPropertyName("total_bytes")]
    public long Total { get; set; }

    [JsonPropertyName("start_time")]
    public DateTimeOffset? StartTime { get; set; }

    [JsonPropertyName("end_time")]
    public DateTimeOffset? EndTime { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>Creator username (frontend <c>creator</c>).</summary>
    [JsonPropertyName("creator")]
    public string Creator { get; set; } = string.Empty;

    [JsonPropertyName("creator_role")]
    public int CreatorRole { get; set; }

    /// <summary>Internal creation timestamp; not serialized (frontend uses <see cref="StartTime"/>).</summary>
    [JsonIgnore]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Internal completion timestamp; not serialized (frontend uses <see cref="EndTime"/>).</summary>
    [JsonIgnore]
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class TaskProgress
{
    public long Current { get; set; }
    public long Total { get; set; }
}
