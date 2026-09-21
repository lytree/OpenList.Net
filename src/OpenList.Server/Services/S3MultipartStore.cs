using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace OpenList.Server.Services;

/// <summary>
/// S3 网关分片上传（Multipart Upload）会话存储。
/// 对应 Go <c>server/s3/multipart.go</c>：分片以流式写入各自的临时文件
/// （大文件无需缓冲进内存），支持乱序上传；Complete 时按 partNumber 升序合并并校验
/// ETag；带 TTL 回收（清理客户端从未 Complete/Abort 而遗留的分片）与崩溃遗留目录清扫。
/// </summary>
public sealed class S3MultipartStore : IDisposable
{
    /// <summary>默认会话空闲 TTL（镜像 Go <c>defaultMultipartTTL = 24h</c>）。</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

    /// <summary>临时文件根目录；每个上传会话一个子目录。</summary>
    internal static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "openlist-s3-multipart");

    /// <summary>单个分片编号上限（S3 规定 1..10000）。</summary>
    public const int MaxPartNumber = 10000;

    private const string DirPrefix = "s3-multipart-";
    private const int CopyBufferSize = 81920;

    private readonly ConcurrentDictionary<string, UploadState> _uploads = new(StringComparer.Ordinal);
    private readonly ILogger<S3MultipartStore> _logger;
    private readonly TimeProvider _time;
    private Timer? _timer;
    private int _gcStarted;

    /// <summary>会话空闲 TTL（由 <c>S3.MultipartTTL</c> 配置覆盖）。</summary>
    public TimeSpan Ttl { get; set; } = DefaultTtl;

    /// <summary>GC 扫描间隔；仅测试用，默认由 TTL 推导。</summary>
    internal TimeSpan GcInterval { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// 创建分片上传会话存储。
    /// </summary>
    /// <param name="logger">日志记录器。</param>
    /// <param name="time">时间源（可选，默认系统时区）。</param>
    public S3MultipartStore(ILogger<S3MultipartStore> logger, TimeProvider? time = null)
    {
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    // --- 会话生命周期 ---

    /// <summary>
    /// 创建一个新的分片上传会话（对应 S3 <c>CreateMultipartUpload</c>），
    /// 返回 uploadId。分片目录建在 <see cref="TempRoot"/> 下。
    /// </summary>
    public string CreateUpload(string bucket, string objectKey)
    {
        Directory.CreateDirectory(TempRoot);
        var uploadId = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(TempRoot, DirPrefix + uploadId);
        Directory.CreateDirectory(dir);

        var now = _time.GetUtcNow();
        _uploads[uploadId] = new UploadState
        {
            UploadId = uploadId,
            Bucket = bucket,
            Object = objectKey,
            Dir = dir,
            Created = now,
            LastActivity = now,
        };
        _logger.LogDebug("s3 multipart: created upload {UploadId} for {Bucket}/{Object}", uploadId, bucket, objectKey);
        return uploadId;
    }

    /// <summary>按 uploadId 查询会话是否存在。</summary>
    public bool Contains(string uploadId) => _uploads.ContainsKey(uploadId);

    /// <summary>列出（可选的）某个桶下的进行中上传。</summary>
    public IReadOnlyList<S3UploadInfo> List(string? bucket = null)
        => _uploads.Values
            .Where(u => bucket is null || string.Equals(u.Bucket, bucket, StringComparison.Ordinal))
            .OrderBy(u => u.Object, StringComparer.Ordinal)
            .ThenBy(u => u.UploadId, StringComparer.Ordinal)
            .Select(u => new S3UploadInfo(u.UploadId, u.Bucket, u.Object, u.Created))
            .ToList();

    /// <summary>丢弃一个上传会话及其分片文件；幂等（不存在时静默返回）。</summary>
    public void Remove(string uploadId)
    {
        if (!_uploads.TryRemove(uploadId, out var state)) return;
        try
        {
            if (!string.IsNullOrEmpty(state.Dir) && Directory.Exists(state.Dir))
                Directory.Delete(state.Dir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "s3 multipart: failed to clean up {Dir}", state.Dir);
        }
    }

    // --- 分片上传 ---

    /// <summary>
    /// 上传单个分片（对应 S3 <c>UploadPart</c>）：流式写入临时文件并同步计算 MD5，
    /// 返回引号包裹的 part ETag。当客户端少传字节（<paramref name="contentLength"/> 未满足）时返回
    /// <c>IncompleteBody</c>，绝不静默存储被截断的数据。
    /// </summary>
    public async Task<UploadPartResult> UploadPartAsync(
        string uploadId,
        int partNumber,
        Stream body,
        long? contentLength,
        CancellationToken ct = default)
    {
        if (partNumber <= 0 || partNumber > MaxPartNumber)
            return UploadPartResult.Fail("InvalidPart", "part number must be between 1 and 10000");
        if (!_uploads.TryGetValue(uploadId, out var state))
            return UploadPartResult.Fail("NoSuchUpload", "upload does not exist");

        var partPath = Path.Combine(state.Dir, $"part-{partNumber:D5}");
        long total = 0;
        string md5Hex;
        try
        {
            await using (var file = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
                try
                {
                    while (true)
                    {
                        var n = await body.ReadAsync(buffer.AsMemory(0, CopyBufferSize), ct);
                        if (n <= 0) break;
                        await file.WriteAsync(buffer.AsMemory(0, n), ct);
                        hash.AppendData(buffer, 0, n);
                        total += n;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                md5Hex = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
        }
        catch (Exception ex)
        {
            TryDeleteFile(partPath);
            return UploadPartResult.Fail("InternalError", ex.Message);
        }

        if (contentLength.HasValue && total != contentLength.Value)
        {
            // 客户端少传了（截断/中断的请求）——镜像 Go 的 ErrIncompleteBody 校验。
            TryDeleteFile(partPath);
            return UploadPartResult.Fail("IncompleteBody", "body shorter than content-length");
        }

        var now = _time.GetUtcNow();
        lock (state.Gate)
        {
            state.Parts[partNumber] = new PartState { Path = partPath, Size = total, Md5Hex = md5Hex };
            state.LastActivity = now;
        }
        _logger.LogDebug("s3 multipart: stored part {PartNumber} for {UploadId} ({Bytes} bytes)", partNumber, uploadId, total);
        return UploadPartResult.Ok($"\"{md5Hex}\"");
    }

    // --- 完成上传 ---

    /// <summary>
    /// 校验 Complete 请求指定的分片，并按 partNumber 升序返回一个可顺序读取所有分片的合并流。
    /// 校验项（对齐 Go <c>CompleteMultipartUpload</c>）：parts 必须升序、每个分片必须存在且 ETag 匹配。
    /// 失败时返回错误码（调用方应保留会话以便客户端重试）。
    /// </summary>
    public CompletePrepareResult PrepareComplete(string uploadId, IReadOnlyList<(int PartNumber, string ETag)> requested)
    {
        if (!_uploads.TryGetValue(uploadId, out var state))
            return CompletePrepareResult.Fail("NoSuchUpload", "upload does not exist");
        if (requested.Count == 0)
            return CompletePrepareResult.Fail("MalformedXML", "complete multipart upload has no parts");

        for (var i = 1; i < requested.Count; i++)
        {
            if (requested[i].PartNumber <= requested[i - 1].PartNumber)
                return CompletePrepareResult.Fail("InvalidPartOrder", "parts must be in ascending part-number order");
        }

        var streams = new List<Stream>(requested.Count);
        var digests = new List<byte[]>(requested.Count);
        long total = 0;
        try
        {
            lock (state.Gate)
            {
                foreach (var p in requested)
                {
                    if (!state.Parts.TryGetValue(p.PartNumber, out var stored))
                        return CompletePrepareResult.Fail("InvalidPart", $"unexpected part number {p.PartNumber} in complete request");
                    if (!string.Equals(p.ETag.Trim('"'), stored.Md5Hex, StringComparison.OrdinalIgnoreCase))
                        return CompletePrepareResult.Fail("InvalidPart", $"unexpected part etag for number {p.PartNumber} in complete request");

                    streams.Add(new FileStream(stored.Path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true));
                    total += stored.Size;
                    digests.Add(Convert.FromHexString(stored.Md5Hex));
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var s in streams) s.Dispose();
            return CompletePrepareResult.Fail("InternalError", ex.Message);
        }

        // S3 multipart ETag = hex(md5(concat(part_md5_digests)))-N
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        foreach (var d in digests) md5.AppendData(d);
        var etag = $"\"{Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant()}-{streams.Count}\"";

        return new CompletePrepareResult { Success = true, Stream = new ConcatStream(streams), Total = total, ETag = etag };
    }

    // --- GC：TTL 回收 + 崩溃遗留清扫 ---

    /// <summary>
    /// 启动 GC：先清扫上次运行遗留的孤儿分片目录，再启动周期性回收空闲超时的会话。
    /// 幂等（重复调用只生效一次）。镜像 Go <c>startReaper</c>。
    /// </summary>
    public void StartGc()
    {
        if (Interlocked.Exchange(ref _gcStarted, 1) == 1) return;
        CleanupStaleDirs(Ttl);
        var interval = GcInterval > TimeSpan.Zero ? GcInterval : DeriveInterval(Ttl);
        _timer = new Timer(_ =>
        {
            try { ReapExpired(); }
            catch (Exception ex) { _logger.LogWarning(ex, "s3 multipart: GC run failed"); }
        }, null, interval, interval);
        _logger.LogInformation("s3 multipart: GC started (ttl={Ttl}, interval={Interval})", Ttl, interval);
    }

    /// <summary>回收空闲时长超过 <see cref="Ttl"/> 的会话（镜像 Go <c>reapExpired</c>）。</summary>
    public void ReapExpired()
    {
        var now = _time.GetUtcNow();
        foreach (var kv in _uploads)
        {
            var state = kv.Value;
            bool expired;
            lock (state.Gate)
            {
                expired = now - state.LastActivity > Ttl;
            }
            if (!expired) continue;
            Remove(kv.Key);
            _logger.LogInformation("s3 multipart: reaped abandoned upload {UploadId} ({Bucket}/{Object})",
                kv.Key, state.Bucket, state.Object);
        }
    }

    /// <summary>由 TTL 推导扫描间隔：TTL/4，夹在 [10s, 1h] 之间（镜像 Go <c>reapInterval</c>）。</summary>
    public static TimeSpan DeriveInterval(TimeSpan ttl)
    {
        var d = TimeSpan.FromTicks(ttl.Ticks / 4);
        if (d < TimeSpan.FromSeconds(10)) d = TimeSpan.FromSeconds(10);
        if (d > TimeSpan.FromHours(1)) d = TimeSpan.FromHours(1);
        return d;
    }

    /// <summary>
    /// 解析 Go duration 风格字符串（如 "24h"、"30m"、"10s"、"1h30m"）。
    /// 空或非法值回退为 <see cref="DefaultTtl"/>。对齐 Go <c>multipartTTL</c> 的容错语义。
    /// </summary>
    public static TimeSpan ParseTtl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DefaultTtl;
        var span = value.Trim().AsSpan();
        var total = TimeSpan.Zero;
        var i = 0;
        var any = false;
        while (i < span.Length)
        {
            var numStart = i;
            while (i < span.Length && (char.IsAsciiDigit(span[i]) || span[i] == '.')) i++;
            if (i == numStart) return DefaultTtl;
            if (!double.TryParse(span[numStart..i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var num))
                return DefaultTtl;
            if (i >= span.Length) return DefaultTtl;
            var unit = span[i++];
            total += unit switch
            {
                'h' => TimeSpan.FromHours(num),
                'm' => TimeSpan.FromMinutes(num),
                's' => TimeSpan.FromSeconds(num),
                'd' => TimeSpan.FromDays(num),
                _ => TimeSpan.MinValue,
            };
            if (total < TimeSpan.Zero) return DefaultTtl;
            any = true;
        }
        return any && total > TimeSpan.Zero ? total : DefaultTtl;
    }

    /// <summary>清扫上次进程运行遗留的孤儿分片目录（镜像 Go <c>cleanupStaleDirs</c>）。</summary>
    private void CleanupStaleDirs(TimeSpan ttl)
    {
        try
        {
            if (!Directory.Exists(TempRoot)) return;
            var cutoff = (_time.GetUtcNow() - ttl).UtcDateTime;
            foreach (var dir in Directory.EnumerateDirectories(TempRoot, DirPrefix + "*"))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(dir) > cutoff) continue;
                    Directory.Delete(dir, recursive: true);
                    _logger.LogInformation("s3 multipart: removed stale multipart dir {Dir}", dir);
                }
                catch { /* 尽力而为 */ }
            }
        }
        catch { /* 尽力而为 */ }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 尽力而为 */ }
    }

    /// <summary>释放周期回收计时器。</summary>
    public void Dispose() => _timer?.Dispose();

    // --- 内部类型 ---

    /// <summary>单个进行中上传会话的状态。</summary>
    internal sealed class UploadState
    {
        public string UploadId { get; init; } = "";
        public string Bucket { get; init; } = "";
        public string Object { get; init; } = "";
        public string Dir { get; init; } = "";
        public DateTimeOffset Created { get; init; }
        public DateTimeOffset LastActivity { get; set; }

        /// <summary>保护 <see cref="Parts"/> 与 <see cref="LastActivity"/> 的同步门。</summary>
        public readonly object Gate = new();
        public readonly Dictionary<int, PartState> Parts = new();
    }

    /// <summary>单个已落盘分片的元信息。</summary>
    internal sealed class PartState
    {
        public string Path { get; init; } = "";
        public long Size { get; init; }
        /// <summary>未加引号的小写十六进制 MD5。</summary>
        public string Md5Hex { get; init; } = "";
    }

    /// <summary>顺序读取多个分片文件的只读流（镜像 Go 的 <c>io.MultiReader</c>）。</summary>
    private sealed class ConcatStream : Stream
    {
        private readonly IReadOnlyList<Stream> _streams;
        private int _index;
        private int _disposed;

        public ConcatStream(IReadOnlyList<Stream> streams) => _streams = streams;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (_index < _streams.Count)
            {
                var n = _streams[_index].Read(buffer, offset, count);
                if (n > 0) return n;
                _streams[_index].Dispose();
                _index++;
            }
            return 0;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_index < _streams.Count)
            {
                var n = await _streams[_index].ReadAsync(buffer, cancellationToken);
                if (n > 0) return n;
                await _streams[_index].DisposeAsync();
                _index++;
            }
            return 0;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                for (; _index < _streams.Count; _index++)
                    _streams[_index].Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

/// <summary>UploadPart 的结果。</summary>
public readonly record struct UploadPartResult(bool Success, string? ETag, string? ErrorCode, string? ErrorMessage)
{
    /// <summary>构造成功结果。</summary>
    public static UploadPartResult Ok(string etag) => new(true, etag, null, null);
    /// <summary>构造失败结果。</summary>
    public static UploadPartResult Fail(string code, string message) => new(false, null, code, message);
}

/// <summary>Complete 前校验 + 合并流准备的结果。</summary>
public sealed class CompletePrepareResult
{
    /// <summary>是否准备成功。</summary>
    public bool Success { get; init; }
    /// <summary>按 partNumber 升序顺序读取全部分片的流（成功后非空，调用方负责释放）。</summary>
    public Stream? Stream { get; init; }
    /// <summary>合并后的总字节数。</summary>
    public long Total { get; init; }
    /// <summary>最终对象的 S3 multipart ETag（引号包裹，形如 <c>"hex-N"</c>）。</summary>
    public string ETag { get; init; } = "";
    /// <summary>失败时的 S3 错误码。</summary>
    public string? ErrorCode { get; init; }
    /// <summary>失败时的错误描述。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>构造失败结果。</summary>
    public static CompletePrepareResult Fail(string code, string message)
        => new() { Success = false, ErrorCode = code, ErrorMessage = message };
}

/// <summary>进行中上传的摘要信息（用于 ListMultipartUploads）。</summary>
public readonly record struct S3UploadInfo(string UploadId, string Bucket, string Object, DateTimeOffset Initiated);
