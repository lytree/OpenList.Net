namespace OpenList.Server.Models;

// Auth
public class LoginReq { public string Username { get; set; } = ""; public string Password { get; set; } = ""; public string? OtpCode { get; set; } }
public class LoginHashReq { public string Username { get; set; } = ""; public string PasswordHash { get; set; } = ""; public string? OtpCode { get; set; } }
public class LoginResp { public string Token { get; set; } = ""; }
public class TwoFAResp { public string QR { get; set; } = ""; public string Secret { get; set; } = ""; }
public class Verify2FAReq { public string Code { get; set; } = ""; public string Secret { get; set; } = ""; }

// Fs
public class ListReq { public string Path { get; set; } = "/"; public bool Refresh { get; set; } public bool WithStorageDetails { get; set; } public string? Password { get; set; } public int Page { get; set; } = 1; public int PerPage { get; set; } = 0; }
public class GetReq { public string Path { get; set; } = "/"; public string? Password { get; set; } }
public class MkdirReq { public string Path { get; set; } = ""; public int Permission { get; set; } }
public class RenameReq { public string Path { get; set; } = ""; public string Name { get; set; } = ""; public bool Overwrite { get; set; } }
public class BatchRenameReq { public List<RenameObject>? RenameObjects { get; set; } }
public class RenameObject { public string SrcPath { get; set; } = ""; public string NewName { get; set; } = ""; }
public class RegexRenameReq { public string SrcDir { get; set; } = ""; public string Regex { get; set; } = ""; public string Replace { get; set; } = ""; }
public class MoveReq { public string SrcDir { get; set; } = ""; public string DstDir { get; set; } = ""; public List<string>? Names { get; set; } public bool Overwrite { get; set; } public bool SkipExisting { get; set; } }
public class RecursiveMoveReq { public List<string>? SrcDirs { get; set; } public string DstDir { get; set; } = ""; }
public class CopyReq { public string SrcDir { get; set; } = ""; public string DstDir { get; set; } = ""; public List<string>? Names { get; set; } public bool Overwrite { get; set; } public bool SkipExisting { get; set; } }
public class RemoveReq { public string Dir { get; set; } = ""; public List<string>? Names { get; set; } }
public class RemoveEmptyDirReq { public string Path { get; set; } = ""; }
public class PutReq { public string Dir { get; set; } = "/"; public string? Remotepath { get; set; } }
public class LinkReq { public string Path { get; set; } = ""; }
public class SearchReq { public string Parent { get; set; } = "/"; public string Keywords { get; set; } = ""; public int Scope { get; set; } public string? Password { get; set; } public int Page { get; set; } = 1; public int PerPage { get; set; } = 20; }
public class DirsReq { public string Path { get; set; } = "/"; public string? Password { get; set; } public bool ForceRoot { get; set; } }
public class OtherReq { public string Path { get; set; } = ""; public string Method { get; set; } = ""; public object? Data { get; set; } }
public class AddOfflineDownloadReq { public List<string>? Urls { get; set; } public string DstDir { get; set; } = ""; public string Tool { get; set; } = "aria2"; public string DeletePolicy { get; set; } = "delete_on_upload_succeed"; }
public class ArchiveDecompressReq { public string SrcDir { get; set; } = ""; public string Name { get; set; } = ""; public string DstDir { get; set; } = ""; public string? Password { get; set; } public string? ArchivePass { get; set; } public string? InnerPath { get; set; } public bool CacheFull { get; set; } public bool PutIntoNewDir { get; set; } public bool Overwrite { get; set; } }
public class DirectUploadReq { public string Path { get; set; } = ""; public string? FileName { get; set; } public long? FileSize { get; set; } public string? Tool { get; set; } }

// Fs responses
public class ListResp { public List<ObjResp> Content { get; set; } = []; public int Total { get; set; } public string? Readme { get; set; } public string? Header { get; set; } public bool Write { get; set; } public bool WriteContentBypass { get; set; } public string? Provider { get; set; } public List<string>? DirectUploadTools { get; set; } }
public class GetResp : ObjResp { public string? RawURL { get; set; } public string? Readme { get; set; } public string? Header { get; set; } public string? Provider { get; set; } public List<ObjResp>? Related { get; set; } }
public class ObjResp { public string Name { get; set; } = ""; public long Size { get; set; } public bool IsDir { get; set; } public DateTime Modified { get; set; } public DateTime Created { get; set; } public string? Sign { get; set; } public string? Thumb { get; set; } public int Type { get; set; } public string? HashInfoStr { get; set; } public Dictionary<string, string>? HashInfo { get; set; } public string ID { get; set; } = ""; public string Path { get; set; } = ""; public string? Thumbnail { get; set; } public object? MountDetails { get; set; } }
public class LinkResp { public string URL { get; set; } = ""; public Dictionary<string, string>? Header { get; set; } }
public class SearchResp { public List<OpenList.Core.Interfaces.SearchResult> Content { get; set; } = []; public int Total { get; set; } }
public class DirsResp { public List<ObjResp> Content { get; set; } = []; }
public class DirectUploadInfo { public string URL { get; set; } = ""; public Dictionary<string, string> Header { get; set; } = new(); }

// Admin
public class CreateUserReq { public string Username { get; set; } = ""; public string? Password { get; set; } public string? BasePath { get; set; } public int Role { get; set; } public int Permission { get; set; } public bool Disabled { get; set; } }
public class UpdateUserReq { public uint Id { get; set; } public string? Username { get; set; } public string? Password { get; set; } public string? BasePath { get; set; } public int? Role { get; set; } public int? Permission { get; set; } public bool? Disabled { get; set; } }
public class DeleteReq { public uint Id { get; set; } }
public class DeleteMetaReq { public string Path { get; set; } = ""; }
public class DeleteShareReq { public string Id { get; set; } = ""; }
public class SendMessageReq { public uint UserId { get; set; } public string Title { get; set; } = ""; public string Content { get; set; } = ""; }
public class ScanReq { public string? Path { get; set; } public double Limit { get; set; } }
public class IdResp { public uint Id { get; set; } }
public class DriverInfoResp { public string Name { get; set; } = ""; public List<OpenList.Core.Drivers.DriverItem> Common { get; set; } = []; public List<OpenList.Core.Drivers.DriverItem> Additional { get; set; } = []; public OpenList.Core.Drivers.DriverConfig Config { get; set; } = new(); }
public class PagedListResp<T> { public List<T> Content { get; set; } = []; public int Total { get; set; } }
public class UserResp { public uint Id { get; set; } public string Username { get; set; } = ""; public int Role { get; set; } public string BasePath { get; set; } = "/"; public int Permission { get; set; } public bool Disabled { get; set; } public bool Otp { get; set; } }

// Me
public class UpdateMeReq { public string? Username { get; set; } public string? Password { get; set; } public string? BasePath { get; set; } }
public class AddSshKeyReq { public string Name { get; set; } = ""; public string Key { get; set; } = ""; }
public class DeleteSshKeyReq { public uint UserId { get; set; } public string Name { get; set; } = ""; }

// Share
public class CreateShareReq { public string FilesRaw { get; set; } = ""; public string? Pwd { get; set; } public DateTime? Expires { get; set; } public int MaxAccessed { get; set; } public string? Remark { get; set; } public string? Readme { get; set; } public string? Header { get; set; } public string? OrderBy { get; set; } public string? OrderDirection { get; set; } public string? ExtractFolder { get; set; } }
public class ShareGetResp { public List<string> Files { get; set; } = []; public string? Readme { get; set; } public string? Header { get; set; } public string? OrderBy { get; set; } public string? OrderDirection { get; set; } public string? ExtractFolder { get; set; } }

// Task
public class TaskIdReq { public string Id { get; set; } = ""; }
public class TaskIdsReq { public List<string>? Ids { get; set; } }

// Archive
public class ArchiveMetaReq { public string Path { get; set; } = ""; public string? Password { get; set; } public bool Refresh { get; set; } public string? ArchivePass { get; set; } }
public class ArchiveListReq { public string Path { get; set; } = ""; public string? Password { get; set; } public string? InnerPath { get; set; } public string? Page { get; set; } public string? PerPage { get; set; } }
public class ArchiveMetaResp { public string Comment { get; set; } = ""; public bool IsEncrypted { get; set; } public List<ArchiveEntryResp> Content { get; set; } = []; }
public class ArchiveEntryResp { public string Name { get; set; } = ""; public long Size { get; set; } public bool IsDir { get; set; } }
public class ArchiveListResp { public List<ArchiveEntryResp> Content { get; set; } = []; public int Total { get; set; } }

// Settings
public class DeleteSettingReq { public string Key { get; set; } = ""; }
public class SetToolSettingReq { public object? Data { get; set; } }

// Public
public class PublicSettingsResp
{
    public string Title { get; set; } = "";
    public string Logo { get; set; } = "";
    public string Favicon { get; set; } = "";
    public string Footer { get; set; } = "";
    public string CustomHead { get; set; } = "";
    public string VideoTypes { get; set; } = "";
    public string AudioTypes { get; set; } = "";
    public string TextTypes { get; set; } = "";
    public bool SsoEnabled { get; set; }
    public string SsoLoginPlatform { get; set; } = "";
    public bool SsoCompatibilityMode { get; set; }
    public bool LdapEnabled { get; set; }
    public string LdapLoginTips { get; set; } = "";
    public bool WebauthnLoginEnabled { get; set; }
    public bool SharePreview { get; set; }
    public bool ShareArchivePreview { get; set; }
    public bool ShowDiskUsageInPlainText { get; set; }
}
