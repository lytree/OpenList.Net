using System.Text.Json.Serialization;

namespace OpenList.Drivers.Cloud.AliyundriveOpen;

/// <summary>
/// 阿里云盘 API 的错误响应体，对齐 Go <c>aliyundrive_open.ErrResp</c>。
/// 该服务成功时也可能带 <c>code</c>（部分接口），因此以 <see cref="Code"/> 是否为空判定成败。
/// </summary>
internal sealed class AliyunErrResp
{
    /// <summary>错误码；为空表示无错误。</summary>
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    /// <summary>错误描述。</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>列目录响应，对齐 Go <c>Files</c>。</summary>
internal sealed class AliyunFiles
{
    /// <summary>本页条目。</summary>
    [JsonPropertyName("items")]
    public List<AliyunFile> Items { get; set; } = [];

    /// <summary>下一页标记；为空表示已到末尾。</summary>
    [JsonPropertyName("next_marker")]
    public string? NextMarker { get; set; }
}

/// <summary>
/// 阿里云盘文件/目录对象，对齐 Go <c>aliyundrive_open.File</c>。
/// <c>type</c> 为 <c>"folder"</c> 时表示目录。
/// </summary>
internal sealed class AliyunFile
{
    /// <summary>所属 drive id。</summary>
    [JsonPropertyName("drive_id")]
    public string? DriveId { get; set; }

    /// <summary>文件 id（驱动内部唯一标识，路径解析的目标）。</summary>
    [JsonPropertyName("file_id")]
    public string? FileId { get; set; }

    /// <summary>父目录 id。</summary>
    [JsonPropertyName("parent_file_id")]
    public string? ParentFileId { get; set; }

    /// <summary>文件/目录名。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>大小（字节；目录为 0）。</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>扩展名（不含点）。</summary>
    [JsonPropertyName("file_extension")]
    public string? FileExtension { get; set; }

    /// <summary>内容哈希（SHA1），用于秒传。</summary>
    [JsonPropertyName("content_hash")]
    public string? ContentHash { get; set; }

    /// <summary>分类（image/video/audio/...）。</summary>
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    /// <summary>类型：<c>folder</c> 或 <c>file</c>。</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>缩略图地址。</summary>
    [JsonPropertyName("thumbnail")]
    public string? Thumbnail { get; set; }

    /// <summary>部分接口返回的直链。</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>创建时间。</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>更新时间。</summary>
    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// 仅创建接口返回：新建对象的文件名。对齐 Go <c>FileName</c> —— 该接口不回 <c>name</c>，
    /// 因此取名字时需要回退到本字段。
    /// </summary>
    [JsonPropertyName("file_name")]
    public string? FileName { get; set; }

    /// <summary>是否目录（对齐 Go <c>fileToObj</c> 的 <c>Type == "folder"</c> 判定）。</summary>
    [JsonIgnore]
    public bool IsFolder => string.Equals(Type, "folder", StringComparison.Ordinal);

    /// <summary>
    /// 有效名称：优先 <c>name</c>，为空时回退 <c>file_name</c>
    /// （创建接口只回后者，对齐 Go <c>fileToObj</c> 的兜底）。
    /// </summary>
    [JsonIgnore]
    public string EffectiveName => string.IsNullOrEmpty(Name) ? (FileName ?? string.Empty) : Name;
}

/// <summary>创建（上传）响应，对齐 Go <c>CreateResp</c>。</summary>
internal sealed class AliyunCreateResp
{
    /// <summary>创建出的文件 id。</summary>
    [JsonPropertyName("file_id")]
    public string? FileId { get; set; }

    /// <summary>分片上传会话 id（走分片流程时才有）。</summary>
    [JsonPropertyName("upload_id")]
    public string? UploadId { get; set; }

    /// <summary>是否命中秒传（服务端已有相同内容）。</summary>
    [JsonPropertyName("rapid_upload")]
    public bool RapidUpload { get; set; }

    /// <summary>分片信息列表（每片的上传地址）。</summary>
    [JsonPropertyName("part_info_list")]
    public List<AliyunPartInfo> PartInfoList { get; set; } = [];
}

/// <summary>单个分片的上传信息，对齐 Go <c>PartInfo</c>。</summary>
internal sealed class AliyunPartInfo
{
    /// <summary>分片序号（从 1 开始）。</summary>
    [JsonPropertyName("part_number")]
    public int PartNumber { get; set; }

    /// <summary>分片上传地址（阿里云 OSS 预签名 URL）。</summary>
    [JsonPropertyName("upload_url")]
    public string? UploadUrl { get; set; }

    /// <summary>分片大小（字节）。</summary>
    [JsonPropertyName("part_size")]
    public long PartSize { get; set; }

    /// <summary>ETag（分片地址下发时为占位值，上传后由服务端回填）。</summary>
    [JsonPropertyName("etag")]
    public string? ETag { get; set; }
}

/// <summary>移动/复制接口响应，对齐 Go <c>MoveOrCopyResp</c>。</summary>
internal sealed class AliyunMoveOrCopyResp
{
    /// <summary>目标位置是否已存在同名对象。</summary>
    [JsonPropertyName("exist")]
    public bool Exist { get; set; }

    /// <summary>所属 drive id。</summary>
    [JsonPropertyName("drive_id")]
    public string? DriveId { get; set; }

    /// <summary>操作后的文件 id（跨 drive 移动时可能变化）。</summary>
    [JsonPropertyName("file_id")]
    public string? FileId { get; set; }
}
