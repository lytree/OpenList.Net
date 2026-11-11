using OpenList.Core.Interfaces;

namespace OpenList.Core.Models;

/// <summary>
/// 后台任务的持久化记录（表 <c>x_tasks</c>）。
/// <para>
/// 运行中的任务本身（工作负载委托）只存在于进程内存；本实体只保存「可跨重启展示」的状态快照，
/// 让 <c>/api/task</c> 的列表与详情在服务重启后仍能查到历史任务。
/// 重启时仍未完成的任务无法续跑（工作负载是闭包，不可序列化），由
/// <see cref="ITaskService.RestoreAsync"/> 统一标记为失败。
/// </para>
/// </summary>
public sealed class BackgroundTask
{
    /// <summary>任务 id（格式 <c>&lt;type&gt;-&lt;序号&gt;</c>）。</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>任务类型。</summary>
    public TaskType Type { get; set; }
    /// <summary>任务名称。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>任务状态。</summary>
    public TaskState State { get; set; } = TaskState.Pending;
    /// <summary>当前进度（字节）。</summary>
    public long Progress { get; set; }
    /// <summary>总量（字节）。</summary>
    public long Total { get; set; }
    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>开始时间。</summary>
    public DateTimeOffset? StartTime { get; set; }
    /// <summary>结束时间。</summary>
    public DateTimeOffset? EndTime { get; set; }
    /// <summary>错误信息。</summary>
    public string? Error { get; set; }
    /// <summary>创建者用户名。</summary>
    public string Creator { get; set; } = string.Empty;
    /// <summary>创建者角色。</summary>
    public int CreatorRole { get; set; }
    /// <summary>创建者用户 id（0 = 系统任务）。</summary>
    public long OwnerId { get; set; }
}
