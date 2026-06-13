using System.Text.Json.Serialization;

namespace OpenList.Server.Models;

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
