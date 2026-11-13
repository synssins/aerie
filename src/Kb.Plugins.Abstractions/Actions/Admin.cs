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

/// <summary>Makes a local account without a password.</summary>
[Action("user.invite")]
public sealed record InviteUser : IAction<InvitedUser>
{
    public required string UserName { get; init; }
    public required string DisplayName { get; init; }
    public string? Email { get; init; }
    public bool IsAdmin { get; init; }
}

/// <summary>Turns local sign-in off for every account but the break-glass one.</summary>
[Action("auth.local.disable")]
public sealed record DisableLocalSignIn : IAction<Unit>;

[Action("settings.public-space.set")]
public sealed record SetPublicSpace : IAction<Unit>
{
    public required bool Enabled { get; init; }
}

public sealed record AdminUserDto(Guid Id, string UserName, string DisplayName, string Source, bool IsAdmin, bool IsBreakGlass, DateTimeOffset CreatedAt, DateTimeOffset? DeactivatedAt, bool CanAnnounce);
public sealed record AuditEntryDto(long Sequence, DateTimeOffset At, string Action, string? Actor, string? TargetType, string? TargetId);
/// <summary>The new account, and the token with which its password is set. The token is part of the answer; it must never reach a log.</summary>
public sealed record InvitedUser(Guid Id, string SetPasswordToken);
