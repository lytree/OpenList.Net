namespace OpenList.Server.Endpoints;

public class MultipartUploadState
{
    public string Bucket { get; set; } = "";
    public string Key { get; set; } = "";
    public Dictionary<int, MultipartPartInfo> Parts { get; set; } = [];
}

public class MultipartPartInfo
{
    public int PartNumber { get; set; }
    public string ETag { get; set; } = "";
    public long Size { get; set; }
    public string? TempFile { get; set; }
}
