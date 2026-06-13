using System.Text.Json.Serialization;

namespace OpenList.Server.Models;

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
