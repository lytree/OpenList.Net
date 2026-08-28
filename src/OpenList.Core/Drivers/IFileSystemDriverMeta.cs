namespace OpenList.Core.Drivers;

/// <summary>描述驱动能力的一批元数据（只读、云端、归档支持等）。</summary>
public interface IFileSystemDriverMeta
{
    /// <summary>是否支持生成下载链接。</summary>
    bool SupportsLink { get; }
    /// <summary>是否支持上传。</summary>
    bool SupportsUpload { get; }
    /// <summary>是否支持读取归档内容。</summary>
    bool SupportsArchiveRead { get; }
    /// <summary>是否支持解压归档。</summary>
    bool SupportsArchiveDecompress { get; }
    /// <summary>是否支持直传。</summary>
    bool SupportsDirectUpload { get; }
}
