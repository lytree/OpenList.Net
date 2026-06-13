using System.Text.Json.Serialization;

namespace OpenList.Server.Models;

public class CreateUserReq { public string Username { get; set; } = ""; public string? Password { get; set; } [JsonPropertyName("base_path")] public string? BasePath { get; set; } public int Role { get; set; } public int Permission { get; set; } public bool Disabled { get; set; } [JsonPropertyName("allow_ldap")] public bool AllowLdap { get; set; } = true; }
public class UpdateUserReq { public uint Id { get; set; } public string? Username { get; set; } public string? Password { get; set; } [JsonPropertyName("base_path")] public string? BasePath { get; set; } public int? Role { get; set; } public int? Permission { get; set; } public bool? Disabled { get; set; } [JsonPropertyName("allow_ldap")] public bool? AllowLdap { get; set; } }
public class SendMessageReq { [JsonPropertyName("user_id")] public uint UserId { get; set; } public string Title { get; set; } = ""; public string Content { get; set; } = ""; }
public class ScanReq { public string? Path { get; set; } public double Limit { get; set; } }
public class IdResp { [JsonPropertyName("id")] public uint Id { get; set; } }
public class DriverInfoResp { public string Name { get; set; } = ""; public List<OpenList.Core.Drivers.DriverItem> Common { get; set; } = []; public List<OpenList.Core.Drivers.DriverItem> Additional { get; set; } = []; public OpenList.Core.Drivers.DriverConfig Config { get; set; } = new(); }
public class PagedListResp<T> { public List<T> Content { get; set; } = []; public int Total { get; set; } }
public class UserResp { public uint Id { get; set; } public string Username { get; set; } = ""; public int Role { get; set; } [JsonPropertyName("base_path")] public string BasePath { get; set; } = "/"; public int Permission { get; set; } public bool Disabled { get; set; } public bool Otp { get; set; } [JsonPropertyName("sso_id")] public string? SsoID { get; set; } [JsonPropertyName("allow_ldap")] public bool AllowLdap { get; set; } public string? Password { get; set; } }
public class UpdateMeReq { public string? Username { get; set; } public string? Password { get; set; } [JsonPropertyName("sso_id")] public string? SsoID { get; set; } }
public class AddSshKeyReq { [JsonPropertyName("title")] public string Name { get; set; } = ""; public string Key { get; set; } = ""; }
public class SetToolSettingReq { public object? Data { get; set; } }
public class UpdateIndexReq { public List<string>? Paths { get; set; } [JsonPropertyName("max_depth")] public int MaxDepth { get; set; } }
