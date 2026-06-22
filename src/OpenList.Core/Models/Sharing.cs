namespace OpenList.Core.Models;

public sealed class Sharing
{
    public long Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public string? Password { get; set; }
    public long? Expiration { get; set; }
    public bool Enabled { get; set; } = true;
    public long OwnerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
