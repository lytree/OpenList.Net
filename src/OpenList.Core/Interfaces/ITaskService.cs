using System.Text.Json.Serialization;
using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// 后台任务类型（复制、上传、转移、下载、解压、离线下载、aria2/qbit/transmission 等）。
/// </summary>
public enum TaskType
{
    /// <summary>复制。</summary>
    Copy,
    /// <summary>上传。</summary>
    Upload,
    /// <summary>转移。</summary>
    Transfer,
    /// <summary>下载。</summary>
    Download,
    /// <summary>解压。</summary>
    Decompress,
    /// <summary>解压后上传。</summary>
    DecompressUpload,
    /// <summary>移动。</summary>
    Move,
    /// <summary>离线下载。</summary>
    OfflineDownload,
    /// <summary>离线下载后转移。</summary>
    OfflineDownloadTransfer,
    /// <summary>aria2 下载。</summary>
    Aria2Down,
    /// <summary>aria2 转移。</summary>
    Aria2Transfer,
    /// <summary>qBittorrent 下载。</summary>
    QbitDown,
    /// <summary>qBittorrent 转移。</summary>
    QbitTransfer,
    /// <summary>Transmission 下载。</summary>
    TransmissionDown,
    /// <summary>Transmission 转移。</summary>
    TransmissionTransfer,
}

/// <summary>
/// 任务生命周期状态。与 OpenList Go <c>pkg/task.TaskState</c> 对应：
/// 该数值正是前端读取的 <c>state</c>。
/// </summary>
public enum TaskState : int
{
    /// <summary>等待中。</summary>
    Pending = 0,
    /// <summary>执行中。</summary>
    Running = 1,
    /// <summary>已成功。</summary>
    Succeeded = 2,
    /// <summary>出错。</summary>
    Errored = 3,
    /// <summary>正在取消。</summary>
    Canceling = 4,
    /// <summary>已取消。</summary>
    Canceled = 5,
}

/// <summary>
/// 后台任务管理服务接口。
/// </summary>
public interface ITaskService
{
    /// <summary>获取单个任务信息。</summary>
    Task<Resp<TaskInfo>> InfoAsync(TaskType type, string tid, CancellationToken ct = default);
    /// <summary>获取已完成的任务列表。</summary>
    Task<Resp<List<TaskInfo>>> DoneAsync(TaskType type, CancellationToken ct = default);
    /// <summary>获取未完成的任务列表。</summary>
    Task<Resp<List<TaskInfo>>> UndoneAsync(TaskType type, CancellationToken ct = default);
    /// <summary>删除单个任务。</summary>
    Task<Resp> DeleteAsync(TaskType type, string tid, CancellationToken ct = default);
    /// <summary>取消单个任务。</summary>
    Task<Resp> CancelAsync(TaskType type, string tid, CancellationToken ct = default);
    /// <summary>清空任务。</summary>
    Task<Resp> ClearAsync(TaskType type, CancellationToken ct = default);
    /// <summary>清空已成功的任务。</summary>
    Task<Resp> ClearSucceededAsync(TaskType type, CancellationToken ct = default);
    /// <summary>重试单个任务。</summary>
    Task<Resp> RetryAsync(TaskType type, string tid, CancellationToken ct = default);
    /// <summary>重试所有失败的任务。</summary>
    Task<Resp> RetryFailedAsync(TaskType type, CancellationToken ct = default);
    /// <summary>批量取消任务；返回按 tid 组织的错误信息（键=tid，值=错误消息）。</summary>
    Task<Resp<Dictionary<string, string>>> CancelSomeAsync(TaskType type, IReadOnlyList<string> tids, CancellationToken ct = default);
    /// <summary>批量删除任务；返回按 tid 组织的错误信息。</summary>
    Task<Resp<Dictionary<string, string>>> DeleteSomeAsync(TaskType type, IReadOnlyList<string> tids, CancellationToken ct = default);
    /// <summary>批量重试任务；返回按 tid 组织的错误信息。</summary>
    Task<Resp<Dictionary<string, string>>> RetrySomeAsync(TaskType type, IReadOnlyList<string> tids, CancellationToken ct = default);
    /// <summary>
    /// 查询任务的创建者 id；任务不存在返回 null。
    /// 任务此前对所有登录用户可见、可取消、可删除，普通用户能中断别人的复制/离线下载；
    /// 端点必须先用本方法校验归属（管理员除外）。
    /// </summary>
    long? OwnerOf(string tid);
    /// <summary>
    /// 创建并开始一个新的异步任务，返回任务 id。
    /// </summary>
    /// <param name="type">任务类型。</param>
    /// <param name="work">任务体。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="ownerId">创建者用户 id（0 表示系统任务，仅管理员可见）。</param>
    Task<string> CreateAsync(TaskType type, Func<IProgress<TaskProgress>, CancellationToken, Task> work, CancellationToken ct = default, long ownerId = 0);
}

/// <summary>
/// 任务信息实体。
/// </summary>
public sealed class TaskInfo
{
    /// <summary>任务 id。</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>任务类型。</summary>
    public TaskType Type { get; set; }
    /// <summary>任务名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>数值化状态，供前端读取（见 <see cref="TaskState"/>）。</summary>
    public TaskState State { get; set; } = TaskState.Pending;

    /// <summary>UI 中展示的可读状态文案（如 "pending"、"running"）。</summary>
    public string Status => State.ToString().ToLowerInvariant();

    /// <summary>当前进度。</summary>
    [JsonPropertyName("progress")]
    public long Progress { get; set; }

    /// <summary>总字节数。</summary>
    [JsonPropertyName("total_bytes")]
    public long Total { get; set; }

    /// <summary>开始时间。</summary>
    [JsonPropertyName("start_time")]
    public DateTimeOffset? StartTime { get; set; }

    /// <summary>结束时间。</summary>
    [JsonPropertyName("end_time")]
    public DateTimeOffset? EndTime { get; set; }

    /// <summary>错误信息，可为空。</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>任务创建者的用户名（前端字段 <c>creator</c>）。</summary>
    [JsonPropertyName("creator")]
    public string Creator { get; set; } = string.Empty;

    /// <summary>
    /// 创建者用户 id。服务端用它判定任务归属（普通用户只能看/管自己的任务），
    /// 不序列化给前端 —— 前端只展示 <see cref="Creator"/>。
    /// </summary>
    [JsonIgnore]
    public long OwnerId { get; set; }

    /// <summary>创建者的角色。</summary>
    [JsonPropertyName("creator_role")]
    public int CreatorRole { get; set; }

    /// <summary>内部创建时间戳，不序列化（前端使用 <see cref="StartTime"/>）。</summary>
    [JsonIgnore]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>内部完成时间戳，不序列化（前端使用 <see cref="EndTime"/>）。</summary>
    [JsonIgnore]
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// 任务进度信息。
/// </summary>
public sealed class TaskProgress
{
    /// <summary>当前已完成量。</summary>
    public long Current { get; set; }
    /// <summary>总量。</summary>
    public long Total { get; set; }
}
