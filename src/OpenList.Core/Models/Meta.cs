using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenList.Core.Models;

/// <summary>
/// Path metadata. Field names mirror the frontend <c>Meta</c> type
/// (snake_case JSON via <see cref="JsonPropertyNameAttribute"/>).
/// <see cref="ReadUsers"/>/<see cref="WriteUsers"/> are stored as JSON
/// strings in the database.
/// </summary>
public sealed class Meta
{
    public long Id { get; set; }

    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("p_sub")]
    public bool PSub { get; set; }

    /// <summary>User ids allowed to read. Stored as JSON in <see cref="ReadUsersJson"/>.</summary>
    [NotMapped]
    [JsonPropertyName("read_users")]
    public List<long> ReadUsers { get; set; } = [];

    [JsonPropertyName("read_users_sub")]
    public bool ReadUsersSub { get; set; }

    /// <summary>User ids allowed to write. Stored as JSON in <see cref="WriteUsersJson"/>.</summary>
    [NotMapped]
    [JsonPropertyName("write_users")]
    public List<long> WriteUsers { get; set; } = [];

    [JsonPropertyName("write_users_sub")]
    public bool WriteUsersSub { get; set; }

    [JsonPropertyName("write")]
    public bool Write { get; set; }

    [JsonPropertyName("w_sub")]
    public bool WSub { get; set; }

    [JsonPropertyName("hide")]
    public string? Hide { get; set; }

    [JsonPropertyName("h_sub")]
    public bool HideSub { get; set; }

    [JsonPropertyName("readme")]
    public string? Readme { get; set; }

    [JsonPropertyName("r_sub")]
    public bool RSub { get; set; }

    [JsonPropertyName("header")]
    public string? Header { get; set; }

    [JsonPropertyName("header_sub")]
    public bool HeaderSub { get; set; }

    // --- EF-mapped JSON columns (not serialized to the API response) ---
    [JsonIgnore]
    public string ReadUsersJson { get; set; } = "[]";

    [JsonIgnore]
    public string WriteUsersJson { get; set; } = "[]";

    /// <summary>Syncs <see cref="ReadUsers"/> with <see cref="ReadUsersJson"/>.</summary>
    public void SyncJson()
    {
        ReadUsersJson = JsonSerializer.Serialize(ReadUsers ?? []);
        WriteUsersJson = JsonSerializer.Serialize(WriteUsers ?? []);
    }

    /// <summary>Hydrates <see cref="ReadUsers"/>/<see cref="WriteUsers"/> from their JSON columns.</summary>
    public void Hydrate()
    {
        try { ReadUsers = string.IsNullOrEmpty(ReadUsersJson) ? [] : JsonSerializer.Deserialize<List<long>>(ReadUsersJson) ?? []; }
        catch { ReadUsers = []; }
        try { WriteUsers = string.IsNullOrEmpty(WriteUsersJson) ? [] : JsonSerializer.Deserialize<List<long>>(WriteUsersJson) ?? []; }
        catch { WriteUsers = []; }
    }
}
