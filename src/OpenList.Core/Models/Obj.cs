namespace OpenList.Core.Models;

public enum ObjMask
{
    None = 0,
    Virtual = 1 << 0,
    NoRename = 1 << 1,
    NoRemove = 1 << 2,
    NoMove = 1 << 3,
    NoCopy = 1 << 4,
    NoWrite = 1 << 5,
    Temp = 1 << 6,
    Locked = NoRename | NoRemove | NoMove,
    ReadOnly = Locked | NoWrite
}

public interface IObj
{
    long GetSize();
    string GetName();
    DateTime GetModTime();
    DateTime GetCreateTime();
    bool IsDir();
    string GetHash();
    string GetID();
    string GetPath();
}

public class ObjItem : IObj
{
    public string ID { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime Modified { get; set; }
    public DateTime CTime { get; set; }
    public bool IsFolder { get; set; }
    public string HashInfo { get; set; } = string.Empty;
    public ObjMask Mask { get; set; }

    public virtual long GetSize() => Size;
    public virtual string GetName() => Name;
    public virtual DateTime GetModTime() => Modified;
    public virtual DateTime GetCreateTime() => CTime == default ? Modified : CTime;
    public virtual bool IsDir() => IsFolder;
    public virtual string GetHash() => HashInfo;
    public virtual string GetID() => ID;
    public virtual string GetPath() => Path;
}

public class ObjWrapName : IObj
{
    public string Name { get; set; } = string.Empty;
    public IObj Inner { get; set; } = null!;

    public long GetSize() => Inner.GetSize();
    public string GetName() => Name;
    public DateTime GetModTime() => Inner.GetModTime();
    public DateTime GetCreateTime() => Inner.GetCreateTime();
    public bool IsDir() => Inner.IsDir();
    public string GetHash() => Inner.GetHash();
    public string GetID() => Inner.GetID();
    public string GetPath() => Inner.GetPath();
}

public class ObjThumb : ObjItem
{
    public string Thumbnail { get; set; } = string.Empty;
}

public class ObjectURL : ObjItem
{
    public string Url { get; set; } = string.Empty;
}

public class ObjThumbURL : ObjItem
{
    public string Thumbnail { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}
