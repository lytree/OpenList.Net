using OpenList.Core.Interfaces;

namespace OpenList.Core.Models;

/// <summary>Persisted task record</summary>
public class TaskRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int State { get; set; } = 0;
    public double Progress { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? PersistData { get; set; }
    public uint? CreatorId { get; set; }

    public TaskInfo ToTaskInfo() => new()
    {
        Id = Id,
        Type = Type,
        Description = Description,
        State = State,
        Progress = Progress,
        Error = Error,
        CreatedAt = CreatedAt,
        PersistData = PersistData,
        CreatorId = CreatorId
    };

    public static TaskRecord FromTaskInfo(TaskInfo info) => new()
    {
        Id = info.Id,
        Type = info.Type,
        Description = info.Description,
        State = info.State,
        Progress = info.Progress,
        Error = info.Error,
        CreatedAt = info.CreatedAt,
        PersistData = info.PersistData,
        CreatorId = info.CreatorId
    };
}

/// <summary>Persisted search index entry</summary>
public class SearchIndex
{
    public int Id { get; set; }
    public string Parent { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDir { get; set; }
    public long Size { get; set; }
    public DateTime Modified { get; set; }
}

/// <summary>Persisted user message</summary>
public class MessageRecord
{
    public int Id { get; set; }
    public uint UserId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool Read { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Token blacklist entry for revoked JWTs</summary>
public class TokenBlacklistEntry
{
    public int Id { get; set; }
    public string Jti { get; set; } = string.Empty;
    public uint UserId { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>WebAuthn/FIDO2 credential stored for a user</summary>
public class WebAuthnCredential
{
    public int Id { get; set; }
    public uint UserId { get; set; }
    public string CredentialId { get; set; } = string.Empty; // Base64Url encoded
    public string PublicKey { get; set; } = string.Empty; // Base64Url encoded
    public string UserHandle { get; set; } = string.Empty; // Base64Url encoded
    public int SignCount { get; set; }
    public string Transports { get; set; } = ""; // JSON array
    public string AaGuid { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
}
