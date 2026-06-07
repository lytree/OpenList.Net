using OpenList.Core.Drivers;
using OpenList.Core.Models;
using System.Text.Json.Serialization;

namespace OpenList.Core.Interfaces;

public interface IFileSystemService
{
    Task<List<IObj>> ListAsync(string path, ListArgs? args = null, CancellationToken ct = default);
    Task<IObj?> GetAsync(string path, CancellationToken ct = default);
    Task<Link?> LinkAsync(string path, LinkArgs args, CancellationToken ct = default);
    Task MakeDirAsync(string path, CancellationToken ct = default);
    Task MoveAsync(string srcPath, string dstDir, CancellationToken ct = default);
    Task CopyAsync(string srcPath, string dstDir, CancellationToken ct = default);
    Task MergeAsync(string srcPath, string dstDir, CancellationToken ct = default);
    Task RenameAsync(string path, string newName, CancellationToken ct = default);
    Task RemoveAsync(string path, CancellationToken ct = default);
    Task PutAsync(string dstDir, FileStreamer file, Action<double> progress, CancellationToken ct = default);
    Task<TaskInfo?> PutAsTaskAsync(string dstDir, FileStreamer file, CancellationToken ct = default);
    Task<object?> OtherAsync(OtherArgs args, CancellationToken ct = default);
    Task<Storage?> GetStorageAsync(string path, CancellationToken ct = default);
    Task<StorageDetails?> GetStorageDetailsAsync(string path, CancellationToken ct = default);
}

public interface IStorageService
{
    Task<(List<Storage> Storages, int Total)> ListStoragesAsync(int page, int perPage, CancellationToken ct = default);
    Task<Storage?> GetStorageByIdAsync(uint id, CancellationToken ct = default);
    Task<uint> CreateStorageAsync(Storage storage, CancellationToken ct = default);
    Task UpdateStorageAsync(Storage storage, CancellationToken ct = default);
    Task DeleteStorageAsync(uint id, CancellationToken ct = default);
    Task EnableStorageAsync(uint id, CancellationToken ct = default);
    Task DisableStorageAsync(uint id, CancellationToken ct = default);
    Task LoadAllStoragesAsync(CancellationToken ct = default);
    IDriver? GetDriverByMountPath(string mountPath);
    DriverInfo GetDriverInfo(string driverName);
    List<DriverInfo> ListDriverInfos();
    List<string> ListDriverNames();
}

public interface IUserService
{
    Task<User?> GetUserByNameAsync(string username, CancellationToken ct = default);
    Task<User?> GetUserByIdAsync(uint id, CancellationToken ct = default);
    Task<(List<User> Users, int Total)> ListUsersAsync(int page, int perPage, CancellationToken ct = default);
    Task<User> CreateUserAsync(User user, CancellationToken ct = default);
    Task UpdateUserAsync(User user, CancellationToken ct = default);
    Task DeleteUserAsync(uint id, CancellationToken ct = default);
    Task SetPasswordAsync(User user, string password, CancellationToken ct = default);
    Task Cancel2FAAsync(uint id, CancellationToken ct = default);
    Task DeleteUserCacheAsync(uint id, CancellationToken ct = default);
    Task DeleteUserCacheByUsernameAsync(string username, CancellationToken ct = default);
    Task<User?> GetUserBySsoIdAsync(string ssoId, CancellationToken ct = default);
    Task<List<SshKey>> ListSshKeysAsync(uint userId, CancellationToken ct = default);
    Task<(List<SshKey> Keys, int Total)> ListSshKeysPagedAsync(uint userId, int page, int perPage, CancellationToken ct = default);
    Task AddSshKeyAsync(uint userId, string name, string key, CancellationToken ct = default);
    Task DeleteSshKeyAsync(uint userId, string name, CancellationToken ct = default);
    Task DeleteSshKeyByIdAsync(uint id, CancellationToken ct = default);
}

public interface IMetaService
{
    Task<(List<Meta> Metas, int Total)> ListMetasAsync(int page = 1, int perPage = 100, CancellationToken ct = default);
    Task<Meta?> GetMetaAsync(string path, CancellationToken ct = default);
    Task<Meta?> GetMetaByIdAsync(uint id, CancellationToken ct = default);
    Task<Meta?> GetNearestMetaAsync(string path, CancellationToken ct = default);
    Task CreateMetaAsync(Meta meta, CancellationToken ct = default);
    Task UpdateMetaAsync(Meta meta, CancellationToken ct = default);
    Task DeleteMetaAsync(string path, CancellationToken ct = default);
    Task DeleteMetaByIdAsync(uint id, CancellationToken ct = default);
}

public interface ISettingService
{
    Task<string?> GetSettingAsync(string key, CancellationToken ct = default);
    Task<SettingItem?> GetSettingItemAsync(string key, CancellationToken ct = default);
    Task<List<SettingItem>> GetSettingsInKeysAsync(List<string> keys, CancellationToken ct = default);
    Task<List<SettingItem>> ListSettingsAsync(CancellationToken ct = default);
    Task<List<SettingItem>> ListSettingsByGroupsAsync(List<int> groups, CancellationToken ct = default);
    Task SaveSettingsAsync(List<SettingItem> settings, CancellationToken ct = default);
    Task DeleteSettingAsync(string key, CancellationToken ct = default);
    Task<List<SettingItem>> DefaultSettingsAsync(CancellationToken ct = default);
    Task<List<SettingItem>> DefaultSettingsByGroupsAsync(List<int> groups, CancellationToken ct = default);
    Task<string> ResetTokenAsync(CancellationToken ct = default);
    bool GetBool(string key);
    string? GetString(string key);
}

public interface ISharingService
{
    Task<(List<Sharing> Sharings, int Total)> ListSharingsAsync(uint userId, int page = 1, int perPage = 100, CancellationToken ct = default);
    Task<Sharing?> GetSharingAsync(string id, CancellationToken ct = default);
    Task<Sharing> CreateSharingAsync(Sharing sharing, CancellationToken ct = default);
    Task UpdateSharingAsync(Sharing sharing, CancellationToken ct = default);
    Task UpdateSharingIdAsync(string oldId, string newId, CancellationToken ct = default);
    Task DeleteSharingAsync(string id, CancellationToken ct = default);
    Task EnableSharingAsync(string id, CancellationToken ct = default);
    Task DisableSharingAsync(string id, CancellationToken ct = default);
    Task<(Sharing? sharing, List<IObj> objs)> ListSharingObjectsAsync(string sid, string path, string? pwd = null, bool refresh = false, CancellationToken ct = default);
    Task<(Sharing? sharing, IObj? obj)> GetSharingObjectAsync(string sid, string path, string? pwd = null, CancellationToken ct = default);
    Task IncrementAccessAsync(string id, string ip, CancellationToken ct = default);
}

public interface IAuthService
{
    Task<string?> LoginAsync(string username, string password, string? otpCode = null, CancellationToken ct = default);
    Task<string?> LoginHashAsync(string username, string passwordHash, string? otpCode = null, CancellationToken ct = default);
    Task<string?> LoginLdapAsync(string username, string password, CancellationToken ct = default);
    Task<User?> GetCurrentUserAsync(string token, CancellationToken ct = default);
    Task<(string qr, string secret)> Generate2FAAsync(uint userId, CancellationToken ct = default);
    Task<bool> Verify2FAAsync(uint userId, string code, string secret, CancellationToken ct = default);
    Task InvalidateTokenAsync(string token, CancellationToken ct = default);
    string HashPassword(string password);
    string GenerateToken(User user);
}

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

public interface ISearchService
{
    Task BuildIndexAsync(CancellationToken ct = default);
    Task UpdateIndexAsync(string parent, List<IObj> objs, CancellationToken ct = default);
    Task<List<SearchResult>> SearchAsync(string parent, string keywords, int page, int perPage, CancellationToken ct = default);
    Task StopIndexAsync(CancellationToken ct = default);
    Task ClearIndexAsync(CancellationToken ct = default);
    Task<IndexProgress> GetProgressAsync(CancellationToken ct = default);
}

public interface ISignService
{
    string Sign(string path);
    string SignArchive(string path);
    bool Verify(string sign, string path);
    bool VerifyArchive(string sign, string path);
}

public interface IMessageService
{
    Task SendMessageAsync(uint userId, string title, string content, CancellationToken ct = default);
    Task<List<Message>> GetMessagesAsync(uint userId, CancellationToken ct = default);
}

public interface IScanService
{
    Task StartScanAsync(string path, double limit = 0, CancellationToken ct = default);
    Task StopScanAsync(CancellationToken ct = default);
    bool IsRunning { get; }
    long ScannedCount { get; }
}

// DTOs
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

public class SearchResult
{
    public string Parent { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    [JsonPropertyName("is_dir")] public bool IsDir { get; set; }
    public long Size { get; set; }
    public DateTime Modified { get; set; }
    public int Type { get; set; }
}

public class IndexProgress
{
    public string State { get; set; } = "";
    public double Progress { get; set; }
    public string? LastError { get; set; }
}

public class Message
{
    public uint Id { get; set; }
    public uint UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool Read { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
