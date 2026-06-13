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
    Task MergeAsync(string srcPath, string dstDir, CancellationToken ct = default);
    Task RenameAsync(string path, string newName, CancellationToken ct = default);
    Task RemoveAsync(string path, CancellationToken ct = default);
    Task PutAsync(string dstDir, FileStreamer file, Action<double> progress, CancellationToken ct = default);
    Task<TaskInfo?> PutAsTaskAsync(string dstDir, FileStreamer file, CancellationToken ct = default);
    Task<object?> OtherAsync(OtherArgs args, CancellationToken ct = default);
    Task<Storage?> GetStorageAsync(string path, CancellationToken ct = default);
    Task<StorageDetails?> GetStorageDetailsAsync(string path, CancellationToken ct = default);
}
