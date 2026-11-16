namespace Kb.Plugins.Actions;

/// <summary>Checks that the audit log's chain is unbroken.</summary>
[Action("audit.verify", Read = true)]
public sealed record VerifyAudit : IAction<AuditVerifyResult>;

/// <summary>The newest audit entries first.</summary>
[Action("audit.list", Read = true)]
public sealed record ListAudit : IAction<IReadOnlyList<AuditEntryDto>>
{
    /// <summary>How many: 1 to 500. Null is 100.</summary>
    public int? Take { get; init; }
    /// <summary>How many of the newest to pass over first: the next page.</summary>
    public int? Offset { get; init; }
    /// <summary>Only actions whose name starts with this ("page." for every page action).</summary>
    public string? Action { get; init; }
    /// <summary>Only what this person did.</summary>
    public Guid? ActorId { get; init; }
    /// <summary>Only from this day, and up to and including that day (UTC).</summary>
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    /// <summary>Only what was done with this API token.</summary>
    public Guid? TokenId { get; init; }
}

[Action("user.list", Read = true)]
public sealed record ListUsers : IAction<IReadOnlyList<AdminUserDto>>;

/// <summary>Turns local sign-in off for every account but the break-glass one.</summary>
[Action("auth.local.disable")]
public sealed record DisableLocalSignIn : IAction<Unit>;

/// <summary>Permissions: the global plugin permissions the person holds ("&lt;plugin id&gt;:&lt;key&gt;").</summary>
public sealed record AdminUserDto(Guid Id, string UserName, string DisplayName, string Source, bool IsAdmin, bool IsBreakGlass, DateTimeOffset CreatedAt, DateTimeOffset? DeactivatedAt, bool CanAnnounce,
    IReadOnlyList<string>? Permissions = null, IReadOnlyList<string>? Groups = null, bool CodesExempt = false);
/// <summary>Via: "API" when it was done with an API token (Token names it), else "Web".</summary>
public sealed record AuditEntryDto(long Sequence, DateTimeOffset At, string Action, string? Actor, string? TargetType, string? TargetId, string Via = "Web", string? Token = null,
    AuditAiSearchDto? AiSearch = null);

/// <summary>One AI search, recorded in its audit entry: the question as sent (its first 2000 characters), the outcome, the pages given to the model. No page text, no answer.</summary>
public sealed record AuditAiSearchDto(string Question, int QuestionLength, string Outcome, Guid? SpaceId, IReadOnlyList<AuditAiSearchPageDto> Pages);

public sealed record AuditAiSearchPageDto(Guid PageId, string Version);
