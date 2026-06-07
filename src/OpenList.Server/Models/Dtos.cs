using System.Text.Json.Serialization;
namespace OpenList.Server.Models;

// Auth
public class LoginReq { public string Username { get; set; } = ""; public string Password { get; set; } = ""; [JsonPropertyName("otp_code")] public string? OtpCode { get; set; } }
public class LoginHashReq { public string Username { get; set; } = ""; [JsonPropertyName("password")] public string PasswordHash { get; set; } = ""; [JsonPropertyName("otp_code")] public string? OtpCode { get; set; } }
public class LoginResp { [JsonPropertyName("token")] public string Token { get; set; } = ""; }
public class TwoFAResp { [JsonPropertyName("qr")] public string QR { get; set; } = ""; public string Secret { get; set; } = ""; }
public class Verify2FAReq { public string Code { get; set; } = ""; public string Secret { get; set; } = ""; }

// Fs
public class ListReq { public string Path { get; set; } = "/"; public bool Refresh { get; set; } public bool WithStorageDetails { get; set; } public string? Password { get; set; } public int Page { get; set; } = 1; [JsonPropertyName("per_page")] public int PerPage { get; set; } = 0; }
public class GetReq { public string Path { get; set; } = "/"; public string? Password { get; set; } }
public class MkdirReq { public string Path { get; set; } = ""; public int Permission { get; set; } }
public class RenameReq { public string Path { get; set; } = ""; public string Name { get; set; } = ""; public bool Overwrite { get; set; } }
public class BatchRenameReq { [JsonPropertyName("src_dir")] public string SrcDir { get; set; } = ""; [JsonPropertyName("rename_objects")] public List<RenameObject>? RenameObjects { get; set; } }
public class RenameObject { [JsonPropertyName("src_name")] public string SrcPath { get; set; } = ""; [JsonPropertyName("new_name")] public string NewName { get; set; } = ""; }
public class RegexRenameReq { [JsonPropertyName("src_dir")] public string SrcDir { get; set; } = ""; [JsonPropertyName("src_name_regex")] public string Regex { get; set; } = ""; [JsonPropertyName("new_name_regex")] public string Replace { get; set; } = ""; }
public class MoveReq { [JsonPropertyName("src_dir")] public string SrcDir { get; set; } = ""; [JsonPropertyName("dst_dir")] public string DstDir { get; set; } = ""; public List<string>? Names { get; set; } public bool Overwrite { get; set; } [JsonPropertyName("skip_existing")] public bool SkipExisting { get; set; } public bool Merge { get; set; } }
public class RecursiveMoveReq { [JsonPropertyName("src_dir")] public string SrcDir { get; set; } = ""; [JsonPropertyName("dst_dir")] public string DstDir { get; set; } = ""; [JsonPropertyName("conflict_policy")] public string? ConflictPolicy { get; set; } }
public class CopyReq { [JsonPropertyName("src_dir")] public string SrcDir { get; set; } = ""; [JsonPropertyName("dst_dir")] public string DstDir { get; set; } = ""; public List<string>? Names { get; set; } public bool Overwrite { get; set; } [JsonPropertyName("skip_existing")] public bool SkipExisting { get; set; } public bool Merge { get; set; } }
public class RemoveReq { public string Dir { get; set; } = ""; public List<string>? Names { get; set; } }
public class RemoveEmptyDirReq { [JsonPropertyName("src_dir")] public string SrcDir { get; set; } = ""; }
public class PutReq { public string Dir { get; set; } = "/"; public string? Remotepath { get; set; } }
public class LinkReq { public string Path { get; set; } = ""; }
public class SearchReq { public string Parent { get; set; } = "/"; public string Keywords { get; set; } = ""; public int Scope { get; set; } public string? Password { get; set; } public int Page { get; set; } = 1; [JsonPropertyName("per_page")] public int PerPage { get; set; } = 20; }
public class DirsReq { public string Path { get; set; } = "/"; public string? Password { get; set; } [JsonPropertyName("force_root")] public bool ForceRoot { get; set; } }
public class OtherReq { public string Path { get; set; } = ""; public string Method { get; set; } = ""; public object? Data { get; set; } public string? Password { get; set; } }
public class AddOfflineDownloadReq { public List<string>? Urls { get; set; } public string Path { get; set; } = ""; public string Tool { get; set; } = "aria2"; [JsonPropertyName("delete_policy")] public string DeletePolicy { get; set; } = "delete_on_upload_succeed"; }
public class ArchiveMetaReq { public string Path { get; set; } = ""; public string? Password { get; set; } public bool Refresh { get; set; } [JsonPropertyName("archive_pass")] public string? ArchivePass { get; set; } }
public class ArchiveListReq { public string Path { get; set; } = ""; public string? Password { get; set; } public bool Refresh { get; set; } [JsonPropertyName("archive_pass")] public string? ArchivePass { get; set; } [JsonPropertyName("inner_path")] public string? InnerPath { get; set; } public int Page { get; set; } = 1; [JsonPropertyName("per_page")] public int PerPage { get; set; } = 100; }
public class ArchiveDecompressReq { [JsonPropertyName("src_dir")] public string SrcDir { get; set; } = ""; [JsonPropertyName("dst_dir")] public string DstDir { get; set; } = ""; [JsonPropertyName("name")] public List<string>? Names { get; set; } [JsonPropertyName("archive_pass")] public string? ArchivePass { get; set; } [JsonPropertyName("inner_path")] public string? InnerPath { get; set; } [JsonPropertyName("cache_full")] public bool CacheFull { get; set; } [JsonPropertyName("put_into_new_dir")] public bool PutIntoNewDir { get; set; } public bool Overwrite { get; set; } }
public class DirectUploadReq { public string Path { get; set; } = ""; public string? FileName { get; set; } public long? FileSize { get; set; } public string? Tool { get; set; } }

// Fs responses
public class ListResp { public List<ObjResp> Content { get; set; } = []; public long Total { get; set; } public string? Readme { get; set; } public string? Header { get; set; } public bool Write { get; set; } [JsonPropertyName("write_content_bypass")] public bool WriteContentBypass { get; set; } public string? Provider { get; set; } [JsonPropertyName("direct_upload_tools")] public List<string>? DirectUploadTools { get; set; } }
public class GetResp : ObjResp { [JsonPropertyName("raw_url")] public string? RawURL { get; set; } public string? Readme { get; set; } public string? Header { get; set; } public string? Provider { get; set; } public List<ObjResp>? Related { get; set; } }
public class ObjResp { public string Name { get; set; } = ""; public long Size { get; set; } [JsonPropertyName("is_dir")] public bool IsDir { get; set; } public DateTime Modified { get; set; } public DateTime Created { get; set; } public string? Sign { get; set; } public string? Thumb { get; set; } public int Type { get; set; } [JsonPropertyName("hashinfo")] public string? HashInfoStr { get; set; } [JsonPropertyName("hash_info")] public Dictionary<string, string>? HashInfo { get; set; } [JsonPropertyName("mount_details")] public OpenList.Core.Models.StorageDetails? MountDetails { get; set; } }
public class LinkResp { public string URL { get; set; } = ""; public Dictionary<string, string>? Header { get; set; } public int Concurrency { get; set; } [JsonPropertyName("part_size")] public int PartSize { get; set; } [JsonPropertyName("content_length")] public long ContentLength { get; set; } }
public class SearchResp { public List<OpenList.Core.Interfaces.SearchResult> Content { get; set; } = []; public int Total { get; set; } }
public class DirResp { public string Name { get; set; } = ""; public DateTime Modified { get; set; } }
public class PageResp<T> { public T? Content { get; set; } public long Total { get; set; } }
public class DirectUploadInfo { public string URL { get; set; } = ""; public Dictionary<string, string> Header { get; set; } = new(); }

// Admin
public class CreateUserReq { public string Username { get; set; } = ""; public string? Password { get; set; } [JsonPropertyName("base_path")] public string? BasePath { get; set; } public int Role { get; set; } public int Permission { get; set; } public bool Disabled { get; set; } [JsonPropertyName("allow_ldap")] public bool AllowLdap { get; set; } = true; }
public class UpdateUserReq { public uint Id { get; set; } public string? Username { get; set; } public string? Password { get; set; } [JsonPropertyName("base_path")] public string? BasePath { get; set; } public int? Role { get; set; } public int? Permission { get; set; } public bool? Disabled { get; set; } [JsonPropertyName("allow_ldap")] public bool? AllowLdap { get; set; } }
public class SendMessageReq { [JsonPropertyName("user_id")] public uint UserId { get; set; } public string Title { get; set; } = ""; public string Content { get; set; } = ""; }
public class ScanReq { public string? Path { get; set; } public double Limit { get; set; } }
public class IdResp { [JsonPropertyName("id")] public uint Id { get; set; } }
public class DriverInfoResp { public string Name { get; set; } = ""; public List<OpenList.Core.Drivers.DriverItem> Common { get; set; } = []; public List<OpenList.Core.Drivers.DriverItem> Additional { get; set; } = []; public OpenList.Core.Drivers.DriverConfig Config { get; set; } = new(); }
public class PagedListResp<T> { public List<T> Content { get; set; } = []; public int Total { get; set; } }
public class UserResp { public uint Id { get; set; } public string Username { get; set; } = ""; public int Role { get; set; } [JsonPropertyName("base_path")] public string BasePath { get; set; } = "/"; public int Permission { get; set; } public bool Disabled { get; set; } public bool Otp { get; set; } [JsonPropertyName("sso_id")] public string? SsoID { get; set; } [JsonPropertyName("allow_ldap")] public bool AllowLdap { get; set; } public string? Password { get; set; } }

// Me
public class UpdateMeReq { public string? Username { get; set; } public string? Password { get; set; } [JsonPropertyName("sso_id")] public string? SsoID { get; set; } }
public class AddSshKeyReq { [JsonPropertyName("title")] public string Name { get; set; } = ""; public string Key { get; set; } = ""; }

// Share
public class CreateShareReq { public List<string>? Files { get; set; } public string? Pwd { get; set; } public DateTime? Expires { get; set; } [JsonPropertyName("max_accessed")] public int MaxAccessed { get; set; } public bool Disabled { get; set; } public string? Remark { get; set; } public string? Readme { get; set; } public string? Header { get; set; } public string? OrderBy { get; set; } public string? OrderDirection { get; set; } public string? ExtractFolder { get; set; } [JsonPropertyName("creator")] public string? CreatorName { get; set; } public string? ID { get; set; } }
public class UpdateShareReq { public string Id { get; set; } = ""; [JsonPropertyName("new_id")] public string? NewID { get; set; } public List<string>? Files { get; set; } public string? Pwd { get; set; } public DateTime? Expires { get; set; } [JsonPropertyName("max_accessed")] public int MaxAccessed { get; set; } [JsonPropertyName("accessed")] public int Accessed { get; set; } public bool Disabled { get; set; } public string? Remark { get; set; } public string? Readme { get; set; } public string? Header { get; set; } [JsonPropertyName("order_by")] public string? OrderBy { get; set; } [JsonPropertyName("order_direction")] public string? OrderDirection { get; set; } [JsonPropertyName("extract_folder")] public string? ExtractFolder { get; set; } [JsonPropertyName("creator")] public string? CreatorName { get; set; } }
public class ShareGetResp { public List<string> Files { get; set; } = []; public string? Readme { get; set; } public string? Header { get; set; } [JsonPropertyName("order_by")] public string? OrderBy { get; set; } [JsonPropertyName("order_direction")] public string? OrderDirection { get; set; } [JsonPropertyName("extract_folder")] public string? ExtractFolder { get; set; } }

// Task
public class TaskIdReq { public string Id { get; set; } = ""; }
public class TaskIdsReq { public List<string>? Ids { get; set; } }

// Archive
public class ArchiveMetaResp { public string Comment { get; set; } = ""; [JsonPropertyName("is_encrypted")] public bool IsEncrypted { get; set; } public List<ArchiveContentResp> Content { get; set; } = []; [JsonPropertyName("raw_url")] public string RawURL { get; set; } = ""; public string Sign { get; set; } = ""; }
public class ArchiveContentResp { public string Name { get; set; } = ""; public long Size { get; set; } [JsonPropertyName("is_dir")] public bool IsDir { get; set; } public DateTime Modified { get; set; } public DateTime Created { get; set; } public string? Sign { get; set; } public string? Thumb { get; set; } public int Type { get; set; } [JsonPropertyName("hashinfo")] public string? HashInfoStr { get; set; } [JsonPropertyName("hash_info")] public Dictionary<string, string>? HashInfo { get; set; } public List<ArchiveContentResp> Children { get; set; } = []; }
public class ArchiveListResp { public List<ObjResp> Content { get; set; } = []; public int Total { get; set; } }

// Settings
public class SetToolSettingReq { public object? Data { get; set; } }
public class UpdateIndexReq { public List<string>? Paths { get; set; } [JsonPropertyName("max_depth")] public int MaxDepth { get; set; } }

// Public
public class PublicSettingsResp
{
    public string Title { get; set; } = "";
    public string Logo { get; set; } = "";
    public string Favicon { get; set; } = "";
    public string Footer { get; set; } = "";
    [JsonPropertyName("custom_head")] public string CustomHead { get; set; } = "";
    [JsonPropertyName("video_types")] public string VideoTypes { get; set; } = "";
    [JsonPropertyName("audio_types")] public string AudioTypes { get; set; } = "";
    [JsonPropertyName("text_types")] public string TextTypes { get; set; } = "";
    [JsonPropertyName("sso_enabled")] public bool SsoEnabled { get; set; }
    [JsonPropertyName("sso_login_platform")] public string SsoLoginPlatform { get; set; } = "";
    [JsonPropertyName("sso_compatibility_mode")] public bool SsoCompatibilityMode { get; set; }
    [JsonPropertyName("ldap_enabled")] public bool LdapEnabled { get; set; }
    [JsonPropertyName("ldap_login_tips")] public string LdapLoginTips { get; set; } = "";
    [JsonPropertyName("webauthn_login_enabled")] public bool WebauthnLoginEnabled { get; set; }
    [JsonPropertyName("share_preview")] public bool SharePreview { get; set; }
    [JsonPropertyName("share_archive_preview")] public bool ShareArchivePreview { get; set; }
    [JsonPropertyName("show_disk_usage_in_plain_text")] public bool ShowDiskUsageInPlainText { get; set; }
}
