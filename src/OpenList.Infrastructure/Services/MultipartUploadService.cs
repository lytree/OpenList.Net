using System.Collections.Concurrent;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 内存型分片上传会话管理器。分片先缓冲到临时文件
/// （<c>Path.GetTempPath()/openlist-multipart/{upload_id}</c>），再按顺序经
/// <see cref="IFileSystemService.PutStreamAsync"/> 冲刷到目标存储。
/// 以与前端分片上传客户端兼容的层级对应 Go 的 <c>internal/multipart</c>
/// （8 分片缓冲窗口、断点续传、可重试失败、滑动 TTL 会话回收）。
/// </summary>
public sealed class MultipartUploadService : IMultipartUploadService, IDisposable
{
    public const int MinChunkSize = 1024 * 1024;          // 1 MB 最小分片
    public const int DefaultChunkSize = 10 * 1024 * 1024; // 10 MB 默认分片上限（multipart_chunk_size 缺省值）
    public const int WindowSize = 8;                      // 缓冲分片的流控窗口

    /// <summary>会话滑动不活动超时（镜像 Go defaultSessionTTL）。</summary>
    public static readonly TimeSpan DefaultSessionTtl = TimeSpan.FromMinutes(30);
    /// <summary>过期会话回收扫描间隔（镜像 Go gcInterval）。</summary>
    public static readonly TimeSpan DefaultGcInterval = TimeSpan.FromMinutes(1);

    /// <summary>临时文件根目录（测试可重定向）。</summary>
    internal static string TempRoot = Path.Combine(Path.GetTempPath(), "openlist-multipart");

    /// <summary>会话不活动超时；仅测试用，默认 <see cref="DefaultSessionTtl"/>。</summary>
    internal TimeSpan SessionTtl { get; set; } = DefaultSessionTtl;
    /// <summary>GC 扫描间隔；仅测试用，默认 <see cref="DefaultGcInterval"/>。</summary>
    internal TimeSpan GcInterval { get; set; } = DefaultGcInterval;

    private readonly IFileSystemService _fs;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, MultipartSession> _sessions = new();
    private readonly object _gcGate = new();
    private bool _gcStarted;
    private ITimer? _gcTimer;
    private bool _disposed;

    /// <summary>
    /// 创建分片上传服务实例。
    /// </summary>
    /// <param name="fs">文件系统服务，用于最终写入分片。</param>
    /// <param name="time">时间源（可选，默认系统时区；用于会话活动时间与 GC 判定）。</param>
    public MultipartUploadService(IFileSystemService fs, TimeProvider? time = null)
    {
        _fs = fs;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// 启动会话 GC：清扫上次运行遗留的临时目录并启动周期回收。
    /// 与 <see cref="InitAsync"/> 内部触发的惰性启动共用一次性门闩，可被多次安全调用
    /// （镜像 Go <c>internal/multipart/session.go</c> 的 StartGC/ensureGC）。
    /// </summary>
    public void StartGc() => EnsureGc();

    /// <summary>一次性启动 GC：清扫上次运行遗留的临时目录并启动周期回收（镜像 Go ensureGC）。</summary>
    private void EnsureGc()
    {
        lock (_gcGate)
        {
            if (_gcStarted) return;
            _gcStarted = true;
            // 清扫上次进程运行遗留的孤儿分片目录（Go StartGC 里的 os.RemoveAll(m.dir())）。
            try { if (Directory.Exists(TempRoot)) Directory.Delete(TempRoot, recursive: true); } catch { /* 尽力而为 */ }
            _gcTimer = _time.CreateTimer(
                _ => { try { Gc(); } catch { /* GC 失败不影响上传 */ } },
                null, GcInterval, GcInterval);
        }
    }

    /// <summary>
    /// 回收超时会话（镜像 Go gc）：把最后活跃时间早于 now - SessionTtl 的会话移除并清理临时目录。
    /// 仅测试用直接调用；生产路径由定时器触发。
    /// </summary>
    internal void Gc()
    {
        var deadline = _time.GetUtcNow() - SessionTtl;
        foreach (var kv in _sessions)
        {
            if (kv.Value.LastActive < deadline && _sessions.TryRemove(kv.Key, out var s))
            {
                lock (s.Sync) { s.State = "aborted"; }
                TryCleanup(s);
            }
        }
    }

    /// <summary>当前活跃会话数（仅测试用）。</summary>
    internal int SessionCount => _sessions.Count;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gcTimer?.Dispose();
    }

    /// <summary>
    /// 初始化分片上传会话。若存在相同路径/大小且未完成、未中止的会话，则复用（断点续传）。
    /// </summary>
    /// <param name="req">分片上传初始化参数（路径、总大小、分片大小）。</param>
    /// <param name="user">发起上传的用户。</param>
    /// <param name="chunkSizeCeilingBytes">分片大小上限（字节）；缺省用默认 10 MB，镜像 Go multipartChunkSize。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>包含上传会话信息的 <c>MultipartInitResp</c>。</returns>
    public Task<Resp<MultipartInitResp>> InitAsync(MultipartInitRequest req, User user, long? chunkSizeCeilingBytes = null, CancellationToken ct = default)
    {
        // 惰性启动 GC（镜像 Go Manager.Init 里的 m.ensureGC()）。
        EnsureGc();

        if (string.IsNullOrWhiteSpace(req.Path))
            return Task.FromResult(Resp<MultipartInitResp>.Fail("path is required", RespCodes.BadRequest));
        if (req.Size < 0)
            return Task.FromResult(Resp<MultipartInitResp>.Fail("invalid size", RespCodes.BadRequest));

        // 生效分片大小：管理员设置是上限，客户端可请求更小但不得超过（镜像 Go multipartChunkSize）。
        var chunkSize = ResolveChunkSize(req.ChunkSize, chunkSizeCeilingBytes);
        var totalChunks = req.Size == 0 ? 1 : (int)Math.Max(1, (req.Size + chunkSize - 1) / chunkSize);

        // 断点续传：路径+大小相同、且未完成/未中止的会话 → 复用已缓冲的分片。
        var existing = _sessions.Values.FirstOrDefault(s =>
            s.Path == req.Path && s.Size == req.Size &&
            (s.State == "receiving" || s.State == "failed_retriable"));
        if (existing is not null)
        {
            lock (existing.Sync)
            {
                if (existing.State == "failed_retriable")
                {
                    existing.Attempt++;
                    existing.State = "receiving";
                    existing.Error = null;
                }
                existing.StorageProgress = 0;
                existing.LastActive = _time.GetUtcNow();
            }
            return Task.FromResult(Resp<MultipartInitResp>.Ok(ToInitResp(existing, resumed: true)));
        }

        var session = new MultipartSession
        {
            UploadId = Guid.NewGuid().ToString("N"),
            Path = req.Path,
            Size = req.Size,
            ChunkSize = chunkSize,
            TotalChunks = totalChunks,
            Attempt = 1,
            State = "receiving",
            LastActive = _time.GetUtcNow(),
            TempDir = Path.Combine(TempRoot, Guid.NewGuid().ToString("N")),
        };
        _sessions[session.UploadId] = session;
        return Task.FromResult(Resp<MultipartInitResp>.Ok(ToInitResp(session, resumed: false)));
    }

    /// <summary>
    /// 解析生效的分片大小。镜像 Go <c>server/handles/multipart.go</c> 的 multipartChunkSize：
    /// 管理员设置的 <c>multipart_chunk_size</c>（MB，此处已换算为字节）为上界，且不低于 1 MB；
    /// 客户端可请求更小分片，但永远不允许更大 —— 服务端每会话缓冲一个分片窗口，
    /// 无上限的客户端建议会直接转化为服务端磁盘占用。
    /// </summary>
    public static long ResolveChunkSize(long requested, long? ceilingBytes)
    {
        var ceiling = ceilingBytes ?? DefaultChunkSize;
        if (ceiling < MinChunkSize) ceiling = MinChunkSize;
        var size = ceiling;
        if (requested > 0 && requested < ceiling)
            size = Math.Max(requested, MinChunkSize);
        return size;
    }

    /// <summary>
    /// 上传单个分片：写入临时文件并登记已收到的分片编号。受缓冲窗口流控（满则返回 429）。
    /// </summary>
    /// <remarks>
    /// 读取请求体必须是异步的：body 在 Kestrel 下是 <c>RateLimitedReadStream</c> 包装的请求流，
    /// 同步 Read 会触发 Kestrel 的 AllowSynchronousIO 异常。因此校验（持锁）→ 异步拷贝（不持锁）
    /// → 登记（持锁）三段式：拷贝期间不占用会话锁，避免并发分片互相阻塞。
    /// </remarks>
    /// <param name="uploadId">上传会话 ID。</param>
    /// <param name="chunkIndex">分片在文件中的序号（从 0 开始）。</param>
    /// <param name="body">分片内容流。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>当前上传快照；会话不存在、窗口已满或分片越界时返回对应错误。</returns>
    public async Task<Resp<MultipartSnapshot>> UploadChunkAsync(string uploadId, int chunkIndex, Stream body, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(uploadId, out var session))
            return Resp<MultipartSnapshot>.Fail("session not found", RespCodes.NotFound);

        // 第一段（持锁）：前置校验 + 流控。
        lock (session.Sync)
        {
            if (session.State == "aborted")
                return Resp<MultipartSnapshot>.Fail("session aborted", RespCodes.Conflict);
            if (session.State == "completed")
                return Resp<MultipartSnapshot>.Ok(ToSnapshot(session));
            if (chunkIndex < 0 || chunkIndex >= session.TotalChunks)
                return Resp<MultipartSnapshot>.Fail("chunk index out of range", RespCodes.Conflict);

            // 流控：服务端窗口在“连续前沿”之外最多缓冲 WindowSize 个分片 ——
            // 前端对 429 会耐心重试。
            if (session.Received.Count - session.Frontier >= WindowSize)
                return Resp<MultipartSnapshot>.Fail("upload window full, retry later", 429);
        }

        // 第二段（不持锁）：异步落临时文件。
        var partFile = PartFile(session, chunkIndex);
        Directory.CreateDirectory(session.TempDir);
        await using (var outStream = new FileStream(
            partFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await body.CopyToAsync(outStream, 81920, ct).ConfigureAwait(false);
        }

        // 第三段（持锁）：登记。会话可能在拷贝期间被 abort/完成，重新检查；
        // 已终结则丢弃刚写的临时分片。
        lock (session.Sync)
        {
            if (session.State is "aborted" or "completed")
            {
                TryDeleteQuietly(partFile);
                return session.State == "aborted"
                    ? Resp<MultipartSnapshot>.Fail("session aborted", RespCodes.Conflict)
                    : Resp<MultipartSnapshot>.Ok(ToSnapshot(session));
            }
            if (session.Received.Add(chunkIndex))
            {
                session.ReceivedBytes += new FileInfo(partFile).Length;
                session.RecomputeFrontier();
            }
            session.Error = null;
            session.LastActive = _time.GetUtcNow();
            return Resp<MultipartSnapshot>.Ok(ToSnapshot(session));
        }
    }

    private static void TryDeleteQuietly(string path)
    {
        try { File.Delete(path); } catch { /* 尽力而为 */ }
    }

    /// <summary>
    /// 查询上传会话的当前状态。
    /// </summary>
    public Task<Resp<MultipartSnapshot>> StatusAsync(string uploadId, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(uploadId, out var session))
            return Task.FromResult(Resp<MultipartSnapshot>.Fail("session not found", RespCodes.NotFound));
        lock (session.Sync)
        {
            session.LastActive = _time.GetUtcNow();
            return Task.FromResult(Resp<MultipartSnapshot>.Ok(ToSnapshot(session)));
        }
    }

    /// <summary>
    /// 完成上传：确认分片齐全后，把临时分片按顺序合并成一个流并写入存储。
    /// </summary>
    /// <param name="uploadId">上传会话 ID。</param>
    /// <param name="user">发起上传的用户。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回完成快照；分片不全或写入失败时返回错误并置为可重试状态。</returns>
    public Task<Resp<MultipartSnapshot>> CompleteAsync(string uploadId, User user, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(uploadId, out var session))
            return Task.FromResult(Resp<MultipartSnapshot>.Fail("session not found", RespCodes.NotFound));

        lock (session.Sync)
        {
            if (session.State == "completed")
                return Task.FromResult(Resp<MultipartSnapshot>.Ok(ToSnapshot(session)));
            if (session.State == "aborted")
                return Task.FromResult(Resp<MultipartSnapshot>.Fail("session aborted", RespCodes.Conflict));

            if (session.Received.Count < session.TotalChunks)
                return Task.FromResult(Resp<MultipartSnapshot>.Fail(
                    $"incomplete upload: {session.Received.Count}/{session.TotalChunks} chunks", RespCodes.BadRequest));

            session.State = "backending";
            session.StorageProgress = 0;
            session.Error = null;
            session.LastActive = _time.GetUtcNow();
        }

        // 在锁外按顺序冲刷分片（涉及网络/存储 IO）。
        var parts = new List<FileStream>();
        try
        {
            for (var i = 0; i < session.TotalChunks; i++)
                parts.Add(new FileStream(PartFile(session, i), FileMode.Open, FileAccess.Read));
            using var combined = new MultiStream(parts);
            var result = _fs.PutStreamAsync(session.Path, combined, user, ct).GetAwaiter().GetResult();
            if (result.Code != 200)
                throw new InvalidOperationException(result.Message);

            lock (session.Sync)
            {
                session.State = "completed";
                session.StorageProgress = 100;
                session.Error = null;
            }
            TryCleanup(session);
            return Task.FromResult(Resp<MultipartSnapshot>.Ok(ToSnapshot(session)));
        }
        catch (Exception ex)
        {
            lock (session.Sync)
            {
                session.State = "failed_retriable";
                session.Error = ex.Message;
            }
            return Task.FromResult(Resp<MultipartSnapshot>.Fail(ex.Message, RespCodes.InternalError));
        }
        finally
        {
            foreach (var p in parts) p.Dispose();
        }
    }

    /// <summary>
    /// 中止上传会话并清理临时分片文件。
    /// </summary>
    public Task<Resp> AbortAsync(string uploadId, CancellationToken ct = default)
    {
        if (!_sessions.TryRemove(uploadId, out var session))
            return Task.FromResult(Resp.Fail("session not found", RespCodes.NotFound));
        lock (session.Sync)
        {
            session.State = "aborted";
        }
        TryCleanup(session);
        return Task.FromResult(Resp.Ok());
    }

    // --- 辅助方法 ---

    /// <summary>分片在临时目录中的落盘路径。</summary>
    private static string PartFile(MultipartSession s, int index) =>
        Path.Combine(s.TempDir, $"{index}.part");

    /// <summary>尽力删除会话临时目录（忽略删除失败）。</summary>
    private static void TryCleanup(MultipartSession s)
    {
        try
        {
            if (Directory.Exists(s.TempDir)) Directory.Delete(s.TempDir, recursive: true);
        }
        catch { /* 尽力而为 */ }
    }

    /// <summary>将会话内部状态转换为对外快照。</summary>
    private static MultipartSnapshot ToSnapshot(MultipartSession s) => new()
    {
        UploadId = s.UploadId,
        State = s.State,
        Attempt = s.Attempt,
        Path = s.Path,
        Size = s.Size,
        ChunkSize = s.ChunkSize,
        TotalChunks = s.TotalChunks,
        Received = ToRanges(s.Received),
        ReceivedBytes = s.ReceivedBytes,
        Frontier = s.Frontier,
        StorageProgress = s.StorageProgress,
        Error = s.Error,
    };

    /// <summary>转换为带断点续传标记的初始化响应。</summary>
    private static MultipartInitResp ToInitResp(MultipartSession s, bool resumed) => new()
    {
        UploadId = s.UploadId,
        State = s.State,
        Attempt = s.Attempt,
        Path = s.Path,
        Size = s.Size,
        ChunkSize = s.ChunkSize,
        TotalChunks = s.TotalChunks,
        Received = ToRanges(s.Received),
        ReceivedBytes = s.ReceivedBytes,
        Frontier = s.Frontier,
        StorageProgress = s.StorageProgress,
        Error = s.Error,
        Resumed = resumed,
    };

    /// <summary>把已收到的分片编号集合压缩为包含式区间 [lo, hi] 列表。</summary>
    private static List<int[]> ToRanges(SortedSet<int> received)
    {
        var ranges = new List<int[]>();
        int? start = null, prev = null;
        foreach (var idx in received)
        {
            if (start is null) { start = idx; prev = idx; continue; }
            if (idx == prev + 1) { prev = idx; continue; }
            ranges.Add([start.Value, prev!.Value]);
            start = idx; prev = idx;
        }
        if (start is not null) ranges.Add([start.Value, prev!.Value]);
        return ranges;
    }

    /// <summary>单个分片上传会话的内部状态。</summary>
    private sealed class MultipartSession
    {
        public readonly object Sync = new();
        public string UploadId { get; init; } = "";
        public string Path { get; init; } = "";
        public long Size { get; init; }
        public long ChunkSize { get; init; }
        public int TotalChunks { get; init; }
        public string TempDir { get; init; } = "";
        public string State { get; set; } = "receiving";
        public int Attempt { get; set; } = 1;
        public SortedSet<int> Received { get; } = new();
        public long ReceivedBytes { get; set; }
        public int Frontier { get; set; }
        public int StorageProgress { get; set; }
        public string? Error { get; set; }
        public DateTimeOffset LastActive { get; set; }

        /// <summary>重算连续前沿（从 0 开始第一个缺失的分片编号）。</summary>
        public void RecomputeFrontier()
        {
            var f = 0;
            while (Received.Contains(f)) f++;
            Frontier = f;
        }
    }

    /// <summary>按顺序把多个子流拼接为单一可读流。</summary>
    private sealed class MultiStream : Stream
    {
        private readonly IEnumerator<Stream> _parts;
        private Stream? _current;
        private bool _disposed;

        public MultiStream(IEnumerable<Stream> parts)
        {
            _parts = parts.GetEnumerator();
            Advance();
        }

        /// <summary>前移到底下一个子流；返回 false 表示已无更多子流。</summary>
        private bool Advance()
        {
            _current?.Dispose();
            if (!_parts.MoveNext()) { _current = null; return false; }
            _current = _parts.Current;
            return true;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_current is null) return 0;
            var n = _current.Read(buffer, offset, count);
            if (n > 0) return n;
            return Advance() ? Read(buffer, offset, count) : 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing) { _current?.Dispose(); _parts.Dispose(); }
            _disposed = true;
            base.Dispose(disposing);
        }
    }
}