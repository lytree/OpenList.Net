using System.Text.Json.Serialization;

namespace OpenList.Core.Models;

/// <summary>
/// 存储（挂载）实体，映射数据库 x_storages 表，表示一个存储驱动的挂载点配置。
/// </summary>
public class Storage
{
    /// <summary>存储主键。</summary>
    public long Id { get; set; }

    /// <summary>挂载路径（虚拟路径，如 "/mount"）。</summary>
    [JsonPropertyName("mount_path")]
    public string MountPath { get; set; } = string.Empty;

    /// <summary>使用的驱动名称（如 "local"、"s3" 等）。</summary>
    public string Driver { get; set; } = string.Empty;
    /// <summary>驱动的附加 JSON 配置，默认空对象。</summary>
    public string Addition { get; set; } = "{}";
    /// <summary>挂载排序权重。</summary>
    public int Order { get; set; }
    /// <summary>是否被禁用。</summary>
    public bool Disabled { get; set; }
    /// <summary>存储备注。</summary>
    public string? Remark { get; set; }

    /// <summary>挂载运行状态（如 "work"、"failed"）。与 Go <c>model.Storage.Status</c> 对应。</summary>
    public string Status { get; set; } = "work";

    /// <summary>列表排序字段。</summary>
    [JsonPropertyName("order_by")]
    public string? OrderBy { get; set; }

    /// <summary>列表排序方向。</summary>
    [JsonPropertyName("order_direction")]
    public string? OrderDirection { get; set; }

    /// <summary>解压目标文件夹。</summary>
    [JsonPropertyName("extract_folder")]
    public string? ExtractFolder { get; set; }

    /// <summary>是否启用 Web 代理。</summary>
    [JsonPropertyName("web_proxy")]
    public bool WebProxy { get; set; }

    /// <summary>WebDAV 策略。</summary>
    [JsonPropertyName("webdav_policy")]
    public string? WebdavPolicy { get; set; }

    /// <summary>是否启用签名访问。</summary>
    [JsonPropertyName("enable_sign")]
    public bool EnableSign { get; set; }

    /// <summary>修改时间。前端读取 JSON <c>modified</c>（ISO 字符串）用于“最后修改”列展示。</summary>
    [JsonPropertyName("modified")]
    public DateTimeOffset Modified { get; set; }

    /// <summary>创建时间（不随 API 序列化）。</summary>
    [JsonIgnore]
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>更新时间（不随 API 序列化）。</summary>
    [JsonIgnore]
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 带驱动展示名称的存储视图，继承自 <see cref="Storage"/>。
/// </summary>
public sealed class StorageWithDriver : Storage
{
    /// <summary>驱动展示名称。</summary>
    [JsonPropertyName("driver_name")]
    public string DriverName { get; set; } = string.Empty;
}
