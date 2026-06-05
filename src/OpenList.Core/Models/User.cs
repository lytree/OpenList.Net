namespace OpenList.Core.Models;

public enum UserRole
{
    General = 0,
    Guest = 1,
    Admin = 2
}

[Flags]
public enum UserPermission
{
    None = 0,
    CanSeeHides = 1 << 0,
    CanAccessWithoutPassword = 1 << 1,
    CanAddOfflineDownloadTasks = 1 << 2,
    CanWriteContent = 1 << 3,
    CanRename = 1 << 4,
    CanMove = 1 << 5,
    CanCopy = 1 << 6,
    CanRemove = 1 << 7,
    CanWebdavRead = 1 << 8,
    CanWebdavManage = 1 << 9,
    CanFTPAccess = 1 << 10,
    CanFTPManage = 1 << 11,
    CanReadArchives = 1 << 12,
    CanDecompress = 1 << 13,
    CanShare = 1 << 14,
    CanCustomizeShareID = 1 << 15
}

public class User
{
    public uint Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PwdHash { get; set; } = string.Empty;
    public long PwdTS { get; set; }
    public string Salt { get; set; } = string.Empty;
    public string BasePath { get; set; } = "/";
    public UserRole Role { get; set; } = UserRole.General;
    public bool Disabled { get; set; }
    public int Permission { get; set; }
    public string? OtpSecret { get; set; }
    public string? SsoID { get; set; }
    public string? Authn { get; set; }
    public bool AllowLdap { get; set; } = true;

    public bool IsGuest => Role == UserRole.Guest;
    public bool IsAdmin => Role == UserRole.Admin;

    public bool HasPermission(UserPermission perm) => (Permission & (int)perm) != 0;

    public string JoinPath(string reqPath)
    {
        if (string.IsNullOrEmpty(BasePath) || BasePath == "/")
            return reqPath;
        return Path.Join(BasePath, reqPath.TrimStart('/'));
    }
}
