namespace OpenList.Core.Models;

public static class Args
{
    public const int DefaultPageSize = 30;
    public const int MaxPageSize = 500;
    public const long MaxUploadSize = 10L * 1024 * 1024 * 1024; // 10 GB
    public const string TablePrefix = "x_";
}
