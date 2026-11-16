using System.Text.Json.Serialization;

namespace OpenList.Core.Models;

/// <summary>
/// 路径元数据。字段名与前端的 <c>Meta</c> 类型对应
/// （通过 <see cref="JsonPropertyNameAttribute"/> 使用 snake_case JSON）。
/// <para>
/// 单用户产品下不再有「按用户」的读写白名单：读取只受<em>隐藏规则</em>与
/// <em>目录密码</em>约束，写入只由「是否已登录」决定。
/// </para>
/// </summary>
public sealed class Meta
{
    /// <summary>元数据主键。</summary>
    public long Id { get; set; }

    /// <summary>对应的完整路径。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>访问密码，可为空。</summary>
    [JsonPropertyName("password")]
    public string? Password { get; set; }

    /// <summary>密码是否对子目录生效。</summary>
    [JsonPropertyName("p_sub")]
    public bool PSub { get; set; }

    /// <summary>需要隐藏的条目名，可为空。</summary>
    [JsonPropertyName("hide")]
    public string? Hide { get; set; }

    /// <summary>隐藏是否对子目录生效。</summary>
    [JsonPropertyName("h_sub")]
    public bool HideSub { get; set; }

    /// <summary>目录 readme 说明。</summary>
    [JsonPropertyName("readme")]
    public string? Readme { get; set; }

    /// <summary>readme 是否对子目录生效。</summary>
    [JsonPropertyName("r_sub")]
    public bool RSub { get; set; }

    /// <summary>目录自定义头部内容。</summary>
    [JsonPropertyName("header")]
    public string? Header { get; set; }

    /// <summary>头部内容是否对子目录生效。</summary>
    [JsonPropertyName("header_sub")]
    public bool HeaderSub { get; set; }

    // --- 兼容列（旧库 x_metas 已存在且为 NOT NULL 无默认值） ---
    // 这 4 列是 OpenList Go 时期按路径配置 WebDAV 策略留下的，本仓库 C# 侧尚未提供
    // 对应功能（全仓无读取点）。但旧库把它们建成 NOT NULL 且无默认值：只要 EF 的
    // INSERT 不带这些列，SQLite 就会报 "NOT NULL constraint failed: x_metas.WebdavPolicy"，
    // 整条 meta 都插不进去。这里映射进模型让 INSERT 带上 0；不参与 JSON 序列化，UpdateAsync
    // 也不覆盖，避免抹掉旧库中的历史取值。
    [JsonIgnore]
    public int WebdavPolicy { get; set; }
    [JsonIgnore]
    public int WebdavRead { get; set; }
    [JsonIgnore]
    public int WebdavWrite { get; set; }
    [JsonIgnore]
    public int WebdavManagement { get; set; }
}
