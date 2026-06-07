namespace OpenList.Core.Models;

using System.Text.Json.Serialization;

public class Meta
{
    public uint Id { get; set; }
    public string Path { get; set; } = string.Empty;
    [JsonPropertyName("read_users")] public List<uint> ReadUsers { get; set; } = [];
    [JsonPropertyName("read_users_sub")] public bool ReadUsersSub { get; set; }
    [JsonPropertyName("write_users")] public List<uint> WriteUsers { get; set; } = [];
    [JsonPropertyName("write_users_sub")] public bool WriteUsersSub { get; set; }
    public string? Password { get; set; }
    [JsonPropertyName("p_sub")] public bool PSub { get; set; }
    public bool Write { get; set; }
    [JsonPropertyName("w_sub")] public bool WSub { get; set; }
    public string? Hide { get; set; }
    [JsonPropertyName("h_sub")] public bool HSub { get; set; }
    public string? Readme { get; set; }
    [JsonPropertyName("r_sub")] public bool RSub { get; set; }
    public string? Header { get; set; }
    [JsonPropertyName("header_sub")] public bool HeaderSub { get; set; }
}
