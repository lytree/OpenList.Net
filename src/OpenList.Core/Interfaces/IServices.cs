using OpenList.Core.Drivers;
using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

public interface IFileSystemService
{
    Task<List<IObj>> ListAsync(string path, ListArgs? args = null, CancellationToken ct = default);
    Task<IObj?> GetAsync(string path, CancellationToken ct = default);
    Task<Link?> LinkAsync(string path, LinkArgs args, CancellationToken ct = default);
    Task MakeDirAsync(string path, CancellationToken ct = default);
    Task MoveAsync(string srcPath, string dstDir, CancellationToken ct = default);
    Task CopyAsync(string srcPath, string dstDir, CancellationToken ct = default);
    Task RenameAsync(string path, string newName, CancellationToken ct = default);
    Task RemoveAsync(string path, CancellationToken ct = default);
    Task PutAsync(string dstDir, FileStreamer file, Action<double> progress, CancellationToken ct = default);
    Task<object?> OtherAsync(OtherArgs args, CancellationToken ct = default);
    Task<Storage?> GetStorageAsync(string path, CancellationToken ct = default);
}

public interface IStorageService
{
    Task<List<Storage>> ListStoragesAsync(int page, int perPage, CancellationToken ct = default);
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
    Task<List<User>> ListUsersAsync(int page, int perPage, CancellationToken ct = default);
    Task<User> CreateUserAsync(User user, CancellationToken ct = default);
    Task UpdateUserAsync(User user, CancellationToken ct = default);
    Task DeleteUserAsync(uint id, CancellationToken ct = default);
    Task Cancel2FAAsync(uint id, CancellationToken ct = default);
    Task DeleteUserCacheAsync(uint id, CancellationToken ct = default);
    Task<List<SshKey>> ListSshKeysAsync(uint userId, CancellationToken ct = default);
    Task AddSshKeyAsync(uint userId, string name, string key, CancellationToken ct = default);
    Task DeleteSshKeyAsync(uint userId, string name, CancellationToken ct = default);
}

public interface IMetaService
{
    Task<List<Meta>> ListMetasAsync(CancellationToken ct = default);
    Task<Meta?> GetMetaAsync(string path, CancellationToken ct = default);
    Task<Meta?> GetNearestMetaAsync(string path, CancellationToken ct = default);
    Task CreateMetaAsync(Meta meta, CancellationToken ct = default);
    Task UpdateMetaAsync(Meta meta, CancellationToken ct = default);
    Task DeleteMetaAsync(string path, CancellationToken ct = default);
}

public interface ISettingService
{
    Task<string?> GetSettingAsync(string key, CancellationToken ct = default);
    Task<List<SettingItem>> ListSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(List<SettingItem> settings, CancellationToken ct = default);
    Task DeleteSettingAsync(string key, CancellationToken ct = default);
    Task DefaultSettingsAsync(CancellationToken ct = default);
    Task ResetTokenAsync(CancellationToken ct = default);
    bool GetBool(string key);
    string? GetString(string key);
}

public interface ISharingService
{
    Task<List<Sharing>> ListSharingsAsync(uint userId, CancellationToken ct = default);
    Task<Sharing?> GetSharingAsync(string id, CancellationToken ct = default);
    Task<Sharing> CreateSharingAsync(Sharing sharing, CancellationToken ct = default);
    Task UpdateSharingAsync(Sharing sharing, CancellationToken ct = default);
    Task DeleteSharingAsync(string id, CancellationToken ct = default);
    Task EnableSharingAsync(string id, CancellationToken ct = default);
    Task DisableSharingAsync(string id, CancellationToken ct = default);
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
}

public interface ITaskService
{
    Task<TaskInfo> CreateTaskAsync(string type, string description, CancellationToken ct = default);
    Task<List<TaskInfo>> ListTasksAsync(string? type = null, CancellationToken ct = default);
    Task<TaskInfo?> GetTaskAsync(string id, CancellationToken ct = default);
    Task CancelTaskAsync(string id, CancellationToken ct = default);
    Task RetryTaskAsync(string id, CancellationToken ct = default);
    Task DeleteTaskAsync(string id, CancellationToken ct = default);
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

// DTOs
public class TaskInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string State { get; set; } = "pending";
    public double Progress { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? PersistData { get; set; }
}

public class SearchResult
{
    public string Parent { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDir { get; set; }
    public long Size { get; set; }
    public DateTime Modified { get; set; }
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
