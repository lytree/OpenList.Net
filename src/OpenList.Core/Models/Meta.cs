using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenList.Core.Models;

/// <summary>
/// 路径元数据。字段名与前端的 <c>Meta</c> 类型对应
/// （通过 <see cref="JsonPropertyNameAttribute"/> 使用 snake_case JSON）。
/// <see cref="ReadUsers"/>/<see cref="WriteUsers"/> 在数据库中存储为 JSON 字符串。
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

    /// <summary>允许读取的用户 id 列表。以 JSON 形式存于 <see cref="ReadUsersJson"/>。</summary>
    [NotMapped]
    [JsonPropertyName("read_users")]
    public List<long> ReadUsers { get; set; } = [];

    /// <summary>读权限是否对子目录生效。</summary>
    [JsonPropertyName("read_users_sub")]
    public bool ReadUsersSub { get; set; }

    /// <summary>允许写入的用户 id 列表。以 JSON 形式存于 <see cref="WriteUsersJson"/>。</summary>
    [NotMapped]
    [JsonPropertyName("write_users")]
    public List<long> WriteUsers { get; set; } = [];

    /// <summary>写权限是否对子目录生效。</summary>
    [JsonPropertyName("write_users_sub")]
    public bool WriteUsersSub { get; set; }

    /// <summary>是否允许写入。</summary>
    [JsonPropertyName("write")]
    public bool Write { get; set; }

    /// <summary>写权限是否对子目录生效。</summary>
    [JsonPropertyName("w_sub")]
    public bool WSub { get; set; }

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

    // --- EF 映射的 JSON 列（不随 API 响应序列化） ---
    /// <summary>读用户列表的 JSON 存储列。</summary>
    [JsonIgnore]
    public string ReadUsersJson { get; set; } = "[]";

    /// <summary>写用户列表的 JSON 存储列。</summary>
    [JsonIgnore]
    public string WriteUsersJson { get; set; } = "[]";

    /// <summary>将 <see cref="ReadUsers"/> 同步到 <see cref="ReadUsersJson"/>、<see cref="WriteUsers"/> 同步到 <see cref="WriteUsersJson"/>。</summary>
    public void SyncJson()
    {
        ReadUsersJson = JsonSerializer.Serialize(ReadUsers ?? []);
        WriteUsersJson = JsonSerializer.Serialize(WriteUsers ?? []);
    }

    /// <summary>从各自 JSON 列反序列化，填充 <see cref="ReadUsers"/>/<see cref="WriteUsers"/>。</summary>
    public void Hydrate()
    {
        try { ReadUsers = string.IsNullOrEmpty(ReadUsersJson) ? [] : JsonSerializer.Deserialize<List<long>>(ReadUsersJson) ?? []; }
        catch { ReadUsers = []; }
        try { WriteUsers = string.IsNullOrEmpty(WriteUsersJson) ? [] : JsonSerializer.Deserialize<List<long>>(WriteUsersJson) ?? []; }
        catch { WriteUsers = []; }
    }
}
