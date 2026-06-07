namespace OpenList.Core.Models;

using System.Text.Json.Serialization;

public class SshKey
{
    public uint Id { get; set; }
    [JsonIgnore] public uint UserId { get; set; }
    [JsonPropertyName("title")] public string Name { get; set; } = string.Empty;
    [JsonIgnore] public string PublicKey { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    [JsonPropertyName("added_time")] public DateTime AddedTime { get; set; }
    [JsonPropertyName("last_used_time")] public DateTime LastUsedTime { get; set; }
    [JsonIgnore] public User? User { get; set; }
}
