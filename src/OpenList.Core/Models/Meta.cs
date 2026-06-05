namespace OpenList.Core.Models;

public class Meta
{
    public uint Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public List<uint> ReadUsers { get; set; } = [];
    public bool ReadUsersSub { get; set; }
    public List<uint> WriteUsers { get; set; } = [];
    public bool WriteUsersSub { get; set; }
    public string? Password { get; set; }
    public bool PSub { get; set; }
    public bool Write { get; set; }
    public bool WSub { get; set; }
    public string? Hide { get; set; }
    public bool HSub { get; set; }
    public string? Readme { get; set; }
    public bool RSub { get; set; }
    public string? Header { get; set; }
    public bool HeaderSub { get; set; }
}
