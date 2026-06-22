namespace OpenList.Core.Drivers;

/// <summary>
/// Streams a file from a driver with optional range support.
/// Used by download/proxy endpoints.
/// </summary>
public sealed class FileStreamer
{
    public Stream Stream { get; set; }
    public long? Length { get; set; }
    public string? ContentType { get; set; }
    public DateTimeOffset? LastModified { get; set; }

    public FileStreamer(Stream stream) => Stream = stream;
}
