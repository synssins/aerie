namespace Kb.Core.Content;

/// <summary>Active: where files are read and written. Target: a move is copying into it. Old: remembered, not used.</summary>
public enum StorageRole { Active, Target, Old }

/// <summary>A place uploaded files can be kept. The database holds file ids, never locations, so changing the active place changes no other row.</summary>
public sealed class StoragePlace
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Kind { get; set; }
    /// <summary>The fields that are not secret, as a JSON object of strings.</summary>
    public string SettingsJson { get; set; } = "{}";
    /// <summary>The secret fields, protected (purpose <c>kb.storage.secret</c>), as one JSON text.</summary>
    public string? SecretsProtected { get; set; }
    public StorageRole Role { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Copying: files are being copied and checked. Copied: ready to try and to switch. Switched and Cancelled are ends.</summary>
public enum StorageMoveState { Copying, Copied, Switched, Cancelled }

/// <summary>Copying every file from the active place to another one, before that one becomes active.</summary>
public sealed class StorageMove
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FromId { get; set; }
    public Guid ToId { get; set; }
    public StorageMoveState State { get; set; }
    public int Total { get; set; }
    public int Copied { get; set; }
    public int Checked { get; set; }
    public int Failed { get; set; }
    /// <summary>"&lt;file id&gt;: &lt;first line of the error&gt;" for the last file that failed.</summary>
    public string? LastError { get; set; }
    /// <summary>This Admin reads their own files from the new place, to try it before the switch.</summary>
    public Guid? PreviewUserId { get; set; }
    /// <summary>The server that is copying, and until when its claim holds. Another server takes over after that.</summary>
    public string? ClaimedBy { get; set; }
    public DateTimeOffset? ClaimedUntil { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
