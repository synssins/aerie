namespace Kb.Core.Audit;

/// <summary>
/// A place audit entries are shipped to. LastSequence is how far it got: entries after it are sent in order,
/// and it moves only when a batch was accepted, so an unreachable target loses nothing.
/// </summary>
public sealed class LogTarget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Kind { get; set; }
    /// <summary>The fields that are not secret, as a JSON object of strings.</summary>
    public string SettingsJson { get; set; } = "{}";
    /// <summary>The secret fields, protected (purpose <c>kb.logship.secret</c>), as one JSON text.</summary>
    public string? SecretsProtected { get; set; }
    public bool Enabled { get; set; } = true;
    public long LastSequence { get; set; }
    public DateTimeOffset? LastShippedAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
    /// <summary>The server that is shipping to this target, and until when its claim holds.</summary>
    public string? ClaimedBy { get; set; }
    public DateTimeOffset? ClaimedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
