using Kb.Plugins.Actions;

namespace Kb.Plugins;

public enum SpaceRole { Reader = 1, Contributor = 2, Supervisor = 3, Owner = 4 }

public enum SpaceAction
{
    Read,
    CreateEdit,
    MoveDeleteOwn,
    MoveDeleteOthers,
    SeeOthersDrafts,
    ReviewTeamHistory,
    ManageBinAgingComments,
    RemoveAttribution,
    ManageRolesSettingsSensitive,
    ApproveAccessRequests,
}

public enum PrincipalType { User, Group }

public enum AgingAudience { Everyone, Supervisors }

public enum DiffOp { Same, Add, Del }

public sealed record DiffLine(DiffOp Op, string Text);

/// <summary>A user's choice for one notification kind.</summary>
public sealed record NotifyPref(bool InApp, bool Email);

public sealed record AuditVerifyResult(bool Valid, long? FirstBrokenSequence);

/// <summary>
/// The only permission entry point handlers and plugins use. It answers for an <see cref="Actor"/>: a signed-in person (narrowed by an API token when the
/// request carried one), a plugin doing background work, or nobody.
/// </summary>
public interface IAccess
{
    /// <summary>The actor's role in a space, or null when the actor has none (answer 404, never 403, so the space is not disclosed).</summary>
    Task<SpaceRole?> RoleAsync(Actor actor, Guid spaceId, CancellationToken ct = default);
    Task<bool> CanAsync(Actor actor, Guid spaceId, SpaceAction action, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> VisibleSpacesAsync(Actor actor, CancellationToken ct = default);
    /// <summary>
    /// Whether the actor holds a permission a plugin declared in its manifest, named "&lt;plugin id&gt;:&lt;key&gt;".
    /// A space permission is held from its minimum role up in that space (the manifest's default role until an Admin changes it).
    /// A global permission is held by the people an Admin gave it to, and by every Admin.
    /// A permission of a plugin that is not loaded, or one nobody declared, is not held.
    /// </summary>
    Task<bool> CanAsync(Actor actor, string permission, Guid? spaceId = null, CancellationToken ct = default);
    /// <summary>True for a signed-in Admin. Never for a request made with an API token, a plugin, or nobody.</summary>
    Task<bool> IsAdminAsync(Actor actor, CancellationToken ct = default);
}
