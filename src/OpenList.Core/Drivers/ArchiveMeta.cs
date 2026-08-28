namespace OpenList.Core.Drivers;

/// <summary>归档元信息文件的标记（完整实现前的占位）。</summary>
public sealed class ArchiveMeta
{
    /// <summary>受支持的归档扩展名列表。</summary>
    public string[] SupportedExtensions { get; set; } = [".zip", ".tar", ".gz", ".rar", ".7z"];
}
