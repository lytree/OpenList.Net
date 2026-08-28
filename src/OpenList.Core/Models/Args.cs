namespace OpenList.Core.Models;

/// <summary>
/// 全局通用常量定义。
/// </summary>
public static class Args
{
    /// <summary>默认分页大小。</summary>
    public const int DefaultPageSize = 30;
    /// <summary>允许的最大分页大小。</summary>
    public const int MaxPageSize = 500;
    /// <summary>最大上传大小限制（10 GB）。</summary>
    public const long MaxUploadSize = 10L * 1024 * 1024 * 1024; // 10 GB
    /// <summary>数据库表名前缀。</summary>
    public const string TablePrefix = "x_";
}
