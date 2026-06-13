using System.Text.Json.Serialization;

namespace OpenList.Core.Interfaces;

public interface ITaskService
{
    Task<TaskInfo> CreateTaskAsync(string type, string description, CancellationToken ct = default);
    Task<TaskInfo> CreateTaskAsync(string type, string description, Func<string, CancellationToken, Task> execute, CancellationToken ct = default);
    Task<List<TaskInfo>> ListTasksAsync(string? type = null, CancellationToken ct = default);
    Task<List<TaskInfo>> ListUndoneTasksAsync(string? type = null, CancellationToken ct = default);
    Task<List<TaskInfo>> ListDoneTasksAsync(string? type = null, CancellationToken ct = default);
    Task<TaskInfo?> GetTaskAsync(string id, CancellationToken ct = default);
    Task CancelTaskAsync(string id, CancellationToken ct = default);
    Task RetryTaskAsync(string id, CancellationToken ct = default);
    Task DeleteTaskAsync(string id, CancellationToken ct = default);
    Task UpdateProgressAsync(string id, double progress, CancellationToken ct = default);
    Task CancelSomeAsync(List<string> ids, CancellationToken ct = default);
    Task DeleteSomeAsync(List<string> ids, CancellationToken ct = default);
    Task RetrySomeAsync(List<string> ids, CancellationToken ct = default);
    Task ClearDoneAsync(uint? userId = null, string? type = null, CancellationToken ct = default);
    Task ClearSucceededAsync(uint? userId = null, string? type = null, CancellationToken ct = default);
    Task RetryFailedAsync(uint? userId = null, string? type = null, CancellationToken ct = default);
}

public enum TaskState
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Canceling = 3,
    Canceled = 4,
    Errored = 5,
    Failing = 6,
    WaitingRetry = 7,
    BeforeRetry = 8,
    Failed = 9
}

public class TaskInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string? Creator { get; set; }
    [JsonIgnore] public uint? CreatorId { get; set; }
    [JsonPropertyName("creator_role")] public int CreatorRole { get; set; } = -1;
    public int State { get; set; } = 0;
    public string? Status { get; set; }
    public double Progress { get; set; }
    [JsonPropertyName("start_time")] public DateTime? StartTime { get; set; }
    [JsonPropertyName("end_time")] public DateTime? EndTime { get; set; }
    [JsonPropertyName("total_bytes")] public long TotalBytes { get; set; }
    public string? Error { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? PersistData { get; set; }
}
