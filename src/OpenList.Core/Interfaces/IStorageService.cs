using System.Text.Json.Serialization;
using OpenList.Core.Models;

namespace OpenList.Core.Interfaces;

/// <summary>
/// 存储驱动挂载管理服务接口。
/// </summary>
public interface IStorageService
{
    /// <summary>分页获取存储列表。</summary>
    Task<Resp<PageResp<StorageWithDriver>>> ListAsync(CancellationToken ct = default);
    /// <summary>按 id 获取单个存储。</summary>
    Task<Resp<StorageWithDriver>> GetAsync(long id, CancellationToken ct = default);
    /// <summary>创建新存储挂载。</summary>
    Task<Resp<Storage>> CreateAsync(CreateStorageReq req, CancellationToken ct = default);
    /// <summary>更新指定存储的配置。</summary>
    Task<Resp> UpdateAsync(long id, CreateStorageReq req, CancellationToken ct = default);
    /// <summary>删除指定存储。</summary>
    Task<Resp> DeleteAsync(long id, CancellationToken ct = default);
    /// <summary>启用指定存储。</summary>
    Task<Resp> EnableAsync(long id, CancellationToken ct = default);
    /// <summary>禁用指定存储。</summary>
    Task<Resp> DisableAsync(long id, CancellationToken ct = default);
    /// <summary>重新加载所有存储挂载。</summary>
    Task<Resp> LoadAllAsync(CancellationToken ct = default);
    /// <summary>获取所有驱动的信息（按驱动名组织）。</summary>
    Task<Resp<Dictionary<string, DriverInfo>>> ListDriversAsync(CancellationToken ct = default);
    /// <summary>获取所有驱动的名称列表。</summary>
    Task<Resp<List<string>>> ListDriverNamesAsync(CancellationToken ct = default);
    /// <summary>获取单个驱动的详细信息。</summary>
    Task<Resp<DriverInfo>> GetDriverInfoAsync(string driverName, CancellationToken ct = default);
}

/// <summary>
/// 创建/更新存储的请求载荷。
/// </summary>
public sealed class CreateStorageReq
{
    /// <summary>存储 id（新建时通常为 0）。</summary>
    [JsonPropertyName("id")]
    public long Id { get; set; }

    /// <summary>挂载路径。</summary>
    [JsonPropertyName("mount_path")]
    public string MountPath { get; set; } = string.Empty;

    /// <summary>使用的驱动名称。</summary>
    public string Driver { get; set; } = string.Empty;
    /// <summary>驱动的附加 JSON 配置。</summary>
    public string Addition { get; set; } = "{}";
    /// <summary>排序权重。</summary>
    public int Order { get; set; }
    /// <summary>是否禁用。</summary>
    public bool Disabled { get; set; }
    /// <summary>备注。</summary>
    public string? Remark { get; set; }

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
}

/// <summary>
/// 驱动元信息，由 <c>/api/admin/driver/list</c> 返回。
/// 与 OpenList Go 的 <c>driver.Info</c> 对应：按驱动名组织的地图，每个值含
/// <see cref="Common"/> 通用字段、<see cref="Additional"/> 驱动特有字段，
/// 以及描述驱动能力的 <see cref="Config"/> 对象。
/// </summary>
public sealed class DriverInfo
{
    /// <summary>通用配置字段。</summary>
    public List<DriverInfoItem> Common { get; set; } = [];
    /// <summary>驱动特有的附加配置字段。</summary>
    public List<DriverInfoItem> Additional { get; set; } = [];
    /// <summary>驱动能力配置。</summary>
    public DriverInfoConfig Config { get; set; } = new();
}

/// <summary>
/// 单个配置字段。所有值均为字符串，以匹配 OpenList Go 的
/// <c>driver.Item</c> 结构（供前端消费）。
/// </summary>
public sealed class DriverInfoItem
{
    /// <summary>字段名。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>字段类型，默认 "string"。</summary>
    public string Type { get; set; } = "string";
    /// <summary>默认值。</summary>
    public string Default { get; set; } = string.Empty;
    /// <summary>候选选项（用于选择类字段）。</summary>
    public string Options { get; set; } = string.Empty;
    /// <summary>是否必填。</summary>
    public bool Required { get; set; }
    /// <summary>帮助说明。</summary>
    public string Help { get; set; } = string.Empty;
}

/// <summary>
/// UI 中展示的驱动能力标志。与 OpenList Go 的 <c>driver.Config</c> 对应。
/// 字段名使用 snake_case 以匹配前端的 DriverConfig 类型。
/// </summary>
public sealed class DriverInfoConfig
{
    /// <summary>驱动展示名称。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>是否本地排序。</summary>
    [JsonPropertyName("local_sort")]
    public bool LocalSort { get; set; }
    /// <summary>是否仅本地。</summary>
    [JsonPropertyName("only_local")]
    public bool OnlyLocal { get; set; }
    /// <summary>是否仅代理。</summary>
    [JsonPropertyName("only_proxy")]
    public bool OnlyProxy { get; set; }
    /// <summary>是否无缓存。</summary>
    [JsonPropertyName("no_cache")]
    public bool NoCache { get; set; }
    /// <summary>是否不支持上传。</summary>
    [JsonPropertyName("no_upload")]
    public bool NoUpload { get; set; }
    /// <summary>是否需要毫秒精度时间。</summary>
    [JsonPropertyName("need_ms")]
    public bool NeedMs { get; set; }
    /// <summary>默认根路径。</summary>
    [JsonPropertyName("default_root")]
    public string DefaultRoot { get; set; } = string.Empty;
    /// <summary>警示信息，可为空。</summary>
    public string? Alert { get; set; }
    /// <summary>是否仅支持索引。（内部标记，不序列化）</summary>
    [JsonIgnore]
    public bool OnlyIndices { get; set; }
    /// <summary>是否优先代理。（内部标记，不序列化）</summary>
    [JsonIgnore]
    public bool PreferProxy { get; set; }
}
