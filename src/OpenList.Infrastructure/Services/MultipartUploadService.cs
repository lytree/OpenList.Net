using System.Collections.Concurrent;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;

namespace OpenList.Infrastructure.Services;

/// <summary>
/// 内存型分片上传会话管理器。分片先缓冲到临时文件
/// （<c>Path.GetTempPath()/openlist-multipart/{upload_id}</c>），再按顺序经
/// <see cref="IFileSystemService.PutStreamAsync"/> 冲刷到目标存储。
/// 以与前端分片上传客户端兼容的层级对应 Go 的 <c>internal/multipart</c>
/// （8 分片缓冲窗口、断点续传、可重试失败）。
/// </summary>
public sealed class MultipartUploadService : IMultipartUploadService
{
    public const int MinChunkSize = 1024 * 1024;          // 1 MB 最小分片
    public const int MaxChunkSize = 100 * 1024 * 1024;    // 100 MB 最大分片
    public const int DefaultChunkSize = 10 * 1024 * 1024; // 10 MB 默认分片
    public const int WindowSize = 8;                      // 缓冲分片的流控窗口

    private readonly IFileSystemService _fs;
    private readonly ConcurrentDictionary<string, MultipartSession> _sessions = new();

    /// <summary>
    /// 创建分片上传服务实例。
    /// </summary>
    /// <param name="fs">文件系统服务，用于最终写入分片。</param>
    public MultipartUploadService(IFileSystemService fs) => _fs = fs;

    /// <summary>
    /// 初始化分片上传会话。若存在相同路径/大小且未完成、未中止的会话，则复用（断点续传）。
    /// </summary>
    /// <param name="req">分片上传初始化参数（路径、总大小、分片大小）。</param>
    /// <param name="user">发起上传的用户。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>包含上传会话信息的 <c>MultipartInitResp</c>。</returns>
    public Task<Resp<MultipartInitResp>> InitAsync(MultipartInitRequest req, User user, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Path))
            return Task.FromResult(Resp<MultipartInitResp>.Fail("path is required", RespCodes.BadRequest));
        if (req.Size < 0)
            return Task.FromResult(Resp<MultipartInitResp>.Fail("invalid size", RespCodes.BadRequest));

        var chunkSize = req.ChunkSize <= 0
            ? DefaultChunkSize
            : Math.Clamp(req.ChunkSize, MinChunkSize, MaxChunkSize);
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
            TempDir = Path.Combine(Path.GetTempPath(), "openlist-multipart", Guid.NewGuid().ToString("N")),
        };
        _sessions[session.UploadId] = session;
        return Task.FromResult(Resp<MultipartInitResp>.Ok(ToInitResp(session, resumed: false)));
    }

    /// <summary>
    /// 上传单个分片：写入临时文件并登记已收到的分片编号。受缓冲窗口流控（满则返回 429）。
    /// </summary>
    /// <param name="uploadId">上传会话 ID。</param>
    /// <param name="chunkIndex">分片在文件中的序号（从 0 开始）。</param>
    /// <param name="body">分片内容流。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>当前上传快照；会话不存在、窗口已满或分片越界时返回对应错误。</returns>
    public Task<Resp<MultipartSnapshot>> UploadChunkAsync(string uploadId, int chunkIndex, Stream body, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(uploadId, out var session))
            return Task.FromResult(Resp<MultipartSnapshot>.Fail("session not found", RespCodes.NotFound));

        lock (session.Sync)
        {
            if (session.State == "aborted")
                return Task.FromResult(Resp<MultipartSnapshot>.Fail("session aborted", RespCodes.Conflict));
            if (session.State == "completed")
                return Task.FromResult(Resp<MultipartSnapshot>.Ok(ToSnapshot(session)));
            if (chunkIndex < 0 || chunkIndex >= session.TotalChunks)
                return Task.FromResult(Resp<MultipartSnapshot>.Fail("chunk index out of range", RespCodes.Conflict));

            // 流控：服务端窗口在“连续前沿”之外最多缓冲 WindowSize 个分片 ——
            // 前端对 429 会耐心重试。
            if (session.Received.Count - session.Frontier >= WindowSize)
                return Task.FromResult(Resp<MultipartSnapshot>.Fail("upload window full, retry later", 429));

            var partFile = PartFile(session, chunkIndex);
            Directory.CreateDirectory(session.TempDir);
            using (var outStream = new FileStream(partFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                body.CopyTo(outStream, 81920);
            }
            if (session.Received.Add(chunkIndex))
            {
                session.ReceivedBytes += new FileInfo(partFile).Length;
                session.RecomputeFrontier();
            }
            session.Error = null;
            return Task.FromResult(Resp<MultipartSnapshot>.Ok(ToSnapshot(session)));
        }
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