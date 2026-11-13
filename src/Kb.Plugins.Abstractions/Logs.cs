namespace Kb.Plugins;

/// <summary>One audit entry as it is shipped to a log file, a syslog server or a SIEM collector.</summary>
public sealed record LogEvent
{
    public required long Sequence { get; init; }
    public required DateTimeOffset At { get; init; }
    public required string Action { get; init; }
    public Guid? ActorId { get; init; }
    /// <summary>The user name of the actor, when the actor is a user that still exists.</summary>
    public string? Actor { get; init; }
    public string? TargetType { get; init; }
    public string? TargetId { get; init; }
    public Guid? SpaceId { get; init; }
    public IReadOnlyDictionary<string, string> Data { get; init; } = new Dictionary<string, string>();
    /// <summary>The hash of the entry in the audit chain, so a collector can check that nothing was changed or left out.</summary>
    public string Hash { get; init; } = "";
    /// <summary>The server that shipped it.</summary>
    public string Server { get; init; } = "";
}

/// <summary>One setting of a log target. Type is "text", "secret" or "select" (with Options).</summary>
public sealed record LogShipField(string Key, string Label, string Type = "text", IReadOnlyList<string>? Options = null);

/// <summary>Sends audit entries somewhere. A call that throws is tried again later with the same entries, in order.</summary>
public interface ILogShipper
{
    Task SendAsync(IReadOnlyList<LogEvent> events, CancellationToken ct);
}

/// <summary>A kind of log target an Admin can add in Administration: a file, syslog, an HTTPS collector, or one a plugin brings.</summary>
public interface ILogShipperKind
{
    string Kind { get; }
    string Label { get; }
    IReadOnlyList<LogShipField> Fields { get; }
    /// <summary>
    /// Opens a target with its settings, secrets already readable. Must not connect.
    /// Throws <see cref="SettingException"/> when a setting is missing or wrong.
    /// </summary>
    ILogShipper Open(IReadOnlyDictionary<string, string?> settings);
}
