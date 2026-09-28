namespace OpenList.Drivers.Cloud;

/// <summary>
/// 把 <see cref="HttpResponseMessage"/> 的响应体暴露为可释放的 <see cref="Stream"/>：
/// 流释放时一并释放响应消息，避免连接在 GC 之前一直被占着。
/// <para>
/// 用于「直链下载」这类不需要限流许可的简单场景；需要持有回调并发许可的场景
/// 请用 <see cref="CallbackRangeReader"/>（它的 <c>CallbackBodyStream</c> 负责归还许可）。
/// </para>
/// </summary>
internal sealed class HttpContentStream : Stream
{
    private readonly HttpResponseMessage _response;
    private readonly Stream _content;
    private int _disposed;

    internal HttpContentStream(HttpResponseMessage response, Stream content)
    {
        _response = response;
        _content = content;
    }

    public override bool CanRead => _content.CanRead;
    public override bool CanSeek => _content.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _content.Length;

    public override long Position
    {
        get => _content.Position;
        set => _content.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => _content.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        => _content.ReadAsync(buffer, ct);

    public override long Seek(long offset, SeekOrigin origin) => _content.Seek(offset, origin);

    public override void Flush() { }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _content.Dispose();
            _response.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _content.DisposeAsync().ConfigureAwait(false);
            _response.Dispose();
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
