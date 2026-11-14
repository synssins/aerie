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
}

[Action("user.list", Read = true)]
public sealed record ListUsers : IAction<IReadOnlyList<AdminUserDto>>;

/// <summary>Turns local sign-in off for every account but the break-glass one.</summary>
[Action("auth.local.disable")]
public sealed record DisableLocalSignIn : IAction<Unit>;

[Action("settings.public-space.set")]
public sealed record SetPublicSpace : IAction<Unit>
{
    public required bool Enabled { get; init; }
}

/// <summary>Permissions: the global plugin permissions the person holds ("&lt;plugin id&gt;:&lt;key&gt;").</summary>
public sealed record AdminUserDto(Guid Id, string UserName, string DisplayName, string Source, bool IsAdmin, bool IsBreakGlass, DateTimeOffset CreatedAt, DateTimeOffset? DeactivatedAt, bool CanAnnounce,
    IReadOnlyList<string>? Permissions = null, IReadOnlyList<string>? Groups = null);
public sealed record AuditEntryDto(long Sequence, DateTimeOffset At, string Action, string? Actor, string? TargetType, string? TargetId);
