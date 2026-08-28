namespace OpenList.Core.Drivers;

/// <summary>
/// 从驱动以流式方式输出文件，可选支持范围请求。
/// 供下载/代理端点使用。
/// </summary>
public sealed class FileStreamer
{
    /// <summary>文件内容流。</summary>
    public Stream Stream { get; set; }
    /// <summary>内容总长度（字节），可为空表示未知。</summary>
    public long? Length { get; set; }
    /// <summary>内容类型（MIME），可为空。</summary>
    public string? ContentType { get; set; }
    /// <summary>最后修改时间，可为空。</summary>
    public DateTimeOffset? LastModified { get; set; }

    /// <summary>用文件流构造一个 FileStreamer。</summary>
    /// <param name="stream">要输出的文件流。</param>
    public FileStreamer(Stream stream) => Stream = stream;
}
