using System;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;

namespace OpenList.Tests;

/// <summary>
/// 分片上传服务：分片大小解析（multipart_chunk_size 上限 + 1 MB 下限）、
/// 会话滑动 TTL 回收（GC）与临时目录清理、启动时孤儿临时目录清扫。
/// 对应 Go <c>internal/multipart</c> 的 multipartChunkSize / session GC。
/// </summary>
[NotInParallel("MultipartService")]
public class MultipartUploadServiceTests
{
    // ---------------- ResolveChunkSize ----------------

    [Test]
    public async Task ResolveChunkSize_Caps_Requested_Size_At_Ceiling()
    {
        // 客户端请求 100 MB，管理员上限 10 MB → 取上限。
        await Assert.That(MultipartUploadService.ResolveChunkSize(100L * 1024 * 1024, 10L * 1024 * 1024))
            .IsEqualTo(10L * 1024 * 1024);
    }

    [Test]
    public async Task ResolveChunkSize_Allows_Smaller_Requested_Size()
    {
        // 客户端请求 2 MB，上限 10 MB → 保留 2 MB（更小被允许）。
        await Assert.That(MultipartUploadService.ResolveChunkSize(2L * 1024 * 1024, 10L * 1024 * 1024))
            .IsEqualTo(2L * 1024 * 1024);
    }

    [Test]
    public async Task ResolveChunkSize_Enforces_Minimum_Chunk_Size()
    {
        // 客户端请求低于 1 MB → 抬升到 1 MB。
        await Assert.That(MultipartUploadService.ResolveChunkSize(64 * 1024, 10L * 1024 * 1024))
            .IsEqualTo(MultipartUploadService.MinChunkSize);
        // 管理员上限低于 1 MB → 抬升到 1 MB。
        await Assert.That(MultipartUploadService.ResolveChunkSize(0, 512 * 1024))
            .IsEqualTo(MultipartUploadService.MinChunkSize);
    }

    [Test]
    public async Task ResolveChunkSize_Uses_Default_Ceiling_When_None_Provided()
    {
        await Assert.That(MultipartUploadService.ResolveChunkSize(0, null))
            .IsEqualTo(MultipartUploadService.DefaultChunkSize);
    }

    // ---------------- Init / chunk size ceiling ----------------

    [Test]
    public async Task InitAsync_Applies_Chunk_Size_Ceiling_And_Computes_Total_Chunks()
    {
        await using var t = new Temp();
        var fs = new FakeFileSystem();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero));
        var svc = new MultipartUploadService(fs, time);

        // 客户端请求 100 MB 分片，上限 10 MB → 10 MB 分片；50 MB 文件共 5 片。
        var r = await svc.InitAsync(new MultipartInitRequest
        {
            Path = "/big.bin",
            Size = 50L * 1024 * 1024,
            ChunkSize = 100L * 1024 * 1024,
        }, new User { Id = 1, Username = "u", Role = UserRole.Admin }, 10L * 1024 * 1024);

        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.ChunkSize).IsEqualTo(10L * 1024 * 1024);
        await Assert.That(r.Data.TotalChunks).IsEqualTo(5);
        await Assert.That(r.Data.Resumed).IsFalse();

        svc.Dispose();
    }

    // ---------------- 会话 GC（滑动 TTL） ----------------

    [Test]
    public async Task Gc_Reaps_Expired_Session_And_Cleans_Temp_Dir()
    {
        await using var t = new Temp();
        MultipartUploadService.TempRoot = t.Root;
        var fs = new FakeFileSystem();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero));
        var svc = new MultipartUploadService(fs, time);

        var init = await svc.InitAsync(new MultipartInitRequest { Path = "/f.bin", Size = 1024 * 1024, ChunkSize = 1024 * 1024 },
            new User { Id = 1, Username = "u", Role = UserRole.Admin });
        await svc.UploadChunkAsync(init.Data!.UploadId, 0, new MemoryStream(new byte[16]));

        // 会话临时目录已创建。
        var dirs = Directory.GetDirectories(t.Root);
        await Assert.That(dirs).Count().IsEqualTo(1);
        await Assert.That(svc.SessionCount).IsEqualTo(1);

        // 超过 30 分钟不活动 → GC 回收会话并清理临时目录。
        time.Advance(TimeSpan.FromMinutes(31));
        svc.Gc();

        await Assert.That(svc.SessionCount).IsEqualTo(0);
        await Assert.That(Directory.GetDirectories(t.Root)).IsEmpty();

        svc.Dispose();
    }

    [Test]
    public async Task Gc_Keeps_Active_Session()
    {
        await using var t = new Temp();
        MultipartUploadService.TempRoot = t.Root;
        var fs = new FakeFileSystem();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero));
        var svc = new MultipartUploadService(fs, time);

        var init = await svc.InitAsync(new MultipartInitRequest { Path = "/f.bin", Size = 1024 * 1024, ChunkSize = 1024 * 1024 },
            new User { Id = 1, Username = "u", Role = UserRole.Admin });

        // 未超过 TTL（例如仅过 10 分钟）→ 会话保留。
        time.Advance(TimeSpan.FromMinutes(10));
        svc.Gc();

        await Assert.That(svc.SessionCount).IsEqualTo(1);
        await Assert.That(svc.StatusAsync(init.Data!.UploadId).Result.Code).IsEqualTo(200);

        svc.Dispose();
    }

    [Test]
    public async Task StartGc_Cleans_Orphan_Temp_Dir_From_Previous_Run()
    {
        await using var t = new Temp();
        MultipartUploadService.TempRoot = t.Root;
        // 模拟上次进程崩溃遗留的孤儿分片目录。
        Directory.CreateDirectory(Path.Combine(t.Root, "orphan-session"));

        var fs = new FakeFileSystem();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero));
        var svc = new MultipartUploadService(fs, time);
        svc.StartGc();

        await Assert.That(Directory.Exists(Path.Combine(t.Root, "orphan-session"))).IsFalse();

        svc.Dispose();
    }

    [Test]
    public async Task Session_LastActive_Refreshes_On_Activity()
    {
        await using var t = new Temp();
        MultipartUploadService.TempRoot = t.Root;
        var fs = new FakeFileSystem();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero));
        var svc = new MultipartUploadService(fs, time);

        var init = await svc.InitAsync(new MultipartInitRequest { Path = "/f.bin", Size = 1024 * 1024, ChunkSize = 1024 * 1024 },
            new User { Id = 1, Username = "u", Role = UserRole.Admin });

        // 每 20 分钟活动一次：第二次活动（40 分钟处）刷新 LastActive，越过首次 TTL 而不被回收。
        time.Advance(TimeSpan.FromMinutes(20));
        await svc.StatusAsync(init.Data!.UploadId);
        time.Advance(TimeSpan.FromMinutes(20));
        await svc.StatusAsync(init.Data!.UploadId);

        svc.Gc();
        await Assert.That(svc.SessionCount).IsEqualTo(1);

        svc.Dispose();
    }

    // --- 测试辅助 ---

    /// <summary>可手动拨动时钟的 TimeProvider（CreateTimer 返回无操作定时器，避免后台线程干扰测试）。</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset start) => _now = start;
        public void Advance(TimeSpan delta) => _now += delta;
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new FakeTimer();

        private sealed class FakeTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>实现 <see cref="IFileSystemService"/> 的最小假件：仅 <see cref="PutStreamAsync"/> 记录调用，其余方法抛不支持。</summary>
    private sealed class FakeFileSystem : IFileSystemService
    {
        public List<string> Writes { get; } = [];

        public Task<Resp> PutStreamAsync(string path, Stream stream, User user, CancellationToken ct = default)
        {
            Writes.Add(path);
            stream.CopyTo(Stream.Null); // 确保多流被完整读取（与真实后端一致）。
            return Task.FromResult(Resp.Ok());
        }

        public Task<Resp<FsListResp>> ListAsync(FsListReq req, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp<FsGetResp>> GetAsync(FsGetReq req, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> MkdirAsync(string path, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> RenameAsync(string oldPath, string newPath, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> BatchRenameAsync(IReadOnlyList<BatchRenameItem> items, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> RegexRenameAsync(string srcDir, string srcReg, string newReg, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> MoveAsync(IReadOnlyList<string> srcPaths, string destDir, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> RecursiveMoveAsync(string srcPath, string destDir, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> CopyAsync(IReadOnlyList<string> srcPaths, string destDir, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp<FsManageResp>> MoveBatchAsync(MoveCopyReq req, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp<FsManageResp>> CopyBatchAsync(MoveCopyReq req, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> RemoveAsync(IReadOnlyList<string> paths, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> RemoveEmptyDirectoryAsync(string path, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> PutFormAsync(string path, Stream stream, string fileName, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp<DirectUploadInfo>> GetDirectUploadInfoAsync(string path, long size, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp<LinkResp>> LinkAsync(string path, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> AddOfflineDownloadAsync(OfflineDownloadReq req, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp<ArchiveMetaResp>> ArchiveMetaAsync(string path, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp<ArchiveListResp>> ArchiveListAsync(string path, string? password, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> ArchiveDecompressAsync(string archivePath, string destPath, string? password, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp<DirsResp>> DirsAsync(string path, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> OtherAsync(string path, string action, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp<SearchResp>> SearchAsync(SearchReq req, User user, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>每个测试独立的临时根目录，测试结束后删除。</summary>
    private sealed class Temp : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "openlist-multipart-test-" + Guid.NewGuid().ToString("N"));

        public ValueTask DisposeAsync()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { /* 尽力而为 */ }
            return ValueTask.CompletedTask;
        }
    }
}
