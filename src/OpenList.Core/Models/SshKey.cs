namespace OpenList.Core.Models;

public class SshKey
{
    public uint Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public uint UserId { get; set; }
    public User? User { get; set; }
}
