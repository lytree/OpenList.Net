namespace OpenList.Core.Models;

public sealed class Meta
{
    public long Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public string? Password { get; set; }
    public bool PSub { get; set; }
    public bool Write { get; set; }
    public string? Header { get; set; }
    public string? Readme { get; set; }
    public bool Hide { get; set; }
    public bool WebdavPolicy { get; set; }
    public bool WebdavRead { get; set; }
    public bool WebdavWrite { get; set; }
    public bool WebdavManagement { get; set; }
}
