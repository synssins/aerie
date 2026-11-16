using Kb.Plugins;

namespace Kb.Core.Spaces;

/// <summary>Spec §7 role matrix: minimum role for each action.</summary>
public static class RoleMatrix
{
    public static SpaceRole MinimumRole(SpaceAction a) => a switch
    {
        SpaceAction.Read => SpaceRole.Reader,
        SpaceAction.CreateEdit => SpaceRole.Contributor,
        SpaceAction.MoveDeleteOwn => SpaceRole.Contributor,
        SpaceAction.MoveDeleteOthers => SpaceRole.Supervisor,
        SpaceAction.SeeOthersDrafts => SpaceRole.Supervisor,
        SpaceAction.ReviewTeamHistory => SpaceRole.Supervisor,
        SpaceAction.ManageBinAgingComments => SpaceRole.Supervisor,
        SpaceAction.RemoveAttribution => SpaceRole.Supervisor,
        // Owners give roles below Owner and change space settings. Admin only, whatever the role: making or removing an Owner,
        // the sensitive and public flags, and the name (SpaceSupport.NeedsAdmin, GrantSpaceRole, RevokeSpaceRole).
        SpaceAction.ManageRolesSettingsSensitive => SpaceRole.Owner,
        SpaceAction.ApproveAccessRequests => SpaceRole.Owner,
        _ => throw new ArgumentOutOfRangeException(nameof(a), a, null),
    };
}

/// <summary>
/// The ten space permissions as a bit set (bit n is <see cref="SpaceAction"/> n), and the four built-in roles.
/// A role is a named set of them; a person holds the union of the sets of all their roles in a space.
/// </summary>
public static class SpacePerms
{
    public static int Of(SpaceAction a) => 1 << (int)a;
    public static bool Has(int mask, SpaceAction a) => (mask & Of(a)) != 0;
    public static readonly int All = Enum.GetValues<SpaceAction>().Aggregate(0, (m, a) => m | Of(a));
    public static readonly int Read = Of(SpaceAction.Read);
    /// <summary>What a Write token may do at most: read, create and edit, move and delete its owner's own pages.</summary>
    public static readonly int WriteToken = Read | Of(SpaceAction.CreateEdit) | Of(SpaceAction.MoveDeleteOwn);

    /// <summary>The permissions a built-in role starts with: every action whose minimum role it reaches.</summary>
    public static int Default(SpaceRole r) => Enum.GetValues<SpaceAction>().Where(a => RoleMatrix.MinimumRole(a) <= r).Aggregate(0, (m, a) => m | Of(a));

    /// <summary>A role that manages members and settings counts as Owner: only an Admin grants, changes or removes it.</summary>
    public static bool IsOwnerGrade(int mask) => Has(mask, SpaceAction.ManageRolesSettingsSensitive);

    public static IReadOnlyList<SpaceAction> Actions(int mask) => [.. Enum.GetValues<SpaceAction>().Where(a => Has(mask, a))];

    /// <summary>The fixed ids of the built-in roles, so a migration and the code name the same rows.</summary>
    public static Guid IdOf(SpaceRole r) => new($"00000000-0000-0000-0000-00000000000{(int)r}");

    /// <summary>
    /// The level a set of permissions reaches, for what still speaks in levels (plugin permissions, the "my role" label):
    /// the strongest built-in role whose permissions are all held. Any access at all is at least Reader.
    /// </summary>
    public static SpaceRole? Rank(int mask, IEnumerable<SpaceRoleDef> roles)
    {
        if (mask == 0) return null;
        var best = SpaceRole.Reader;
        foreach (var r in roles)
            if (r.BuiltIn is { } level && level > best && (r.Permissions & ~mask) == 0) best = level;
        return best;
    }
}

/// <summary>A role: a name and a set of space permissions. BuiltIn: one of the four the product starts with (never deleted).</summary>
public sealed class SpaceRoleDef
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public int Permissions { get; set; }
    public SpaceRole? BuiltIn { get; set; }
}

public sealed class Space
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Key { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool IsSensitive { get; set; }
    public SpaceVisibility Visibility { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>The space's switch for page comments (comments.md, Switching comments on and off).</summary>
    public bool CommentsEnabled { get; set; } = true;
    /// <summary>The space's switch for page ratings (ratings.md).</summary>
    public bool RatingsEnabled { get; set; } = true;
    public int AgingDays { get; set; } = 180;
    public AgingAudience AgingAudience { get; set; } = AgingAudience.Everyone;
    /// <summary>Recycle-bin retention override; null uses Kb:Retention:RecycleDays.</summary>
    public int? RecycleDays { get; set; }
    /// <summary>The review cycle a new page placed here starts with (months, 0 none); null: none set.</summary>
    public int? ReviewMonths { get; set; }
    /// <summary>
    /// Set when an Admin deleted the space: it is in the recycle bin, with everything in it. A deleted space is not there for anyone,
    /// by list or by id; its pages, files and roles stay as they were and give nothing until an Admin restores it.
    /// </summary>
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
}

public sealed class SpaceRoleAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SpaceId { get; set; }
    public PrincipalType PrincipalType { get; set; }
    /// <summary>User id (Guid as string) or group id (directory group object id / local group id).</summary>
    public required string PrincipalId { get; set; }
    public Guid RoleId { get; set; }
}

/// <summary>Time-boxed or approved access that bypasses the "admins have no silent access to sensitive spaces" rule.</summary>
public sealed class SpaceAccessGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SpaceId { get; set; }
    public Guid UserId { get; set; }
    public SpaceRole Role { get; set; }
    public bool IsEmergency { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>Facts about a user that permission decisions need.</summary>
public sealed record PrincipalInfo(Guid UserId, bool IsAdmin, bool HasFullAccess, IReadOnlyCollection<string> GroupIds);

/// <summary>Data access needed by the permission service. Implemented in Kb.Data.</summary>
public interface ISpaceAccessData
{
    Task<PrincipalInfo?> GetPrincipalAsync(Guid userId, CancellationToken ct);
    Task<Space?> GetSpaceAsync(Guid spaceId, CancellationToken ct);
    Task<IReadOnlyList<Space>> GetAllSpacesAsync(CancellationToken ct);
    Task<IReadOnlyList<SpaceRoleAssignment>> GetAssignmentsAsync(Guid spaceId, CancellationToken ct);
    Task<IReadOnlyList<SpaceRoleDef>> GetRoleDefsAsync(CancellationToken ct);
    Task<IReadOnlyList<SpaceRoleAssignment>> GetAssignmentsForPrincipalsAsync(IReadOnlyCollection<string> userAndGroupIds, CancellationToken ct);
    Task<IReadOnlyList<SpaceAccessGrant>> GetActiveGrantsAsync(Guid userId, DateTimeOffset now, CancellationToken ct);
}

public interface ISpacePermissionService
{
    /// <summary>The level the person's permissions reach (see <see cref="SpacePerms.Rank"/>); null for no access.</summary>
    Task<SpaceRole?> GetRoleAsync(Guid userId, Guid spaceId, CancellationToken ct = default);
    /// <summary>The person's permissions in the space as a bit set (<see cref="SpacePerms"/>); 0 for no access.</summary>
    Task<int> GetPermissionsAsync(Guid userId, Guid spaceId, CancellationToken ct = default);
    Task<bool> CanAsync(Guid userId, Guid spaceId, SpaceAction action, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> VisibleSpaceIdsAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>
/// Effective role = highest of: direct and group assignments; admin implicit Owner on non-sensitive spaces;
/// approved or unexpired emergency grants. Everyone signed in reads a space that is SignedIn or Anyone (as Reader), whatever else they hold.
/// Scoped lifetime: caches nothing across requests.
/// </summary>
public sealed class SpacePermissionService(ISpaceAccessData data, TimeProvider time) : ISpacePermissionService
{
    public async Task<SpaceRole?> GetRoleAsync(Guid userId, Guid spaceId, CancellationToken ct = default) =>
        SpacePerms.Rank(await GetPermissionsAsync(userId, spaceId, ct), await data.GetRoleDefsAsync(ct));

    /// <summary>
    /// The level of the roles the person holds in the space (assignments, through groups too, and grants), without the Owner role an Admin
    /// has on every space that is not sensitive. Comment moderation and the rating list need a held Supervisor or Owner role:
    /// being an Admin is not enough on its own (comments.md, ratings.md; questions Q-0024, Q-0029).
    /// </summary>
    public async Task<SpaceRole?> GetHeldRoleAsync(Guid userId, Guid spaceId, CancellationToken ct = default) =>
        SpacePerms.Rank(await MaskAsync(userId, spaceId, adminOverride: false, ct), await data.GetRoleDefsAsync(ct));

    public Task<int> GetPermissionsAsync(Guid userId, Guid spaceId, CancellationToken ct = default) => MaskAsync(userId, spaceId, adminOverride: true, ct);

    private async Task<int> MaskAsync(Guid userId, Guid spaceId, bool adminOverride, CancellationToken ct)
    {
        var p = await data.GetPrincipalAsync(userId, ct);
        var space = await data.GetSpaceAsync(spaceId, ct);
        if (p is null || space is null) return 0;
        // A sensitive space is always Private; the check here holds even if a row says otherwise.
        var mask = space.Visibility != SpaceVisibility.Private && !space.IsSensitive ? SpacePerms.Read : 0;
        if (!p.HasFullAccess) return mask;

        // The union of every role the person holds there, directly and through groups. A role that is gone gives nothing.
        var roles = (await data.GetRoleDefsAsync(ct)).ToDictionary(r => r.Id);
        var ids = PrincipalIds(p);
        foreach (var a in await data.GetAssignmentsAsync(spaceId, ct))
            if (ids.Contains(a.PrincipalId) && roles.TryGetValue(a.RoleId, out var role)) mask |= role.Permissions;

        if (adminOverride && p.IsAdmin && !space.IsSensitive) mask = SpacePerms.All;

        // Emergency access and approved requests are at a built-in level: that role's permissions as they are now.
        foreach (var g in await data.GetActiveGrantsAsync(userId, time.GetUtcNow(), ct))
            if (g.SpaceId == spaceId) mask |= roles.TryGetValue(SpacePerms.IdOf(g.Role), out var builtIn) ? builtIn.Permissions : SpacePerms.Default(g.Role);

        return mask;
    }

    public async Task<bool> CanAsync(Guid userId, Guid spaceId, SpaceAction action, CancellationToken ct = default) =>
        SpacePerms.Has(await GetPermissionsAsync(userId, spaceId, ct), action);

    public async Task<IReadOnlyList<Guid>> VisibleSpaceIdsAsync(Guid userId, CancellationToken ct = default)
    {
        var p = await data.GetPrincipalAsync(userId, ct);
        if (p is null) return [];
        var spaces = await data.GetAllSpacesAsync(ct);
        var visible = new HashSet<Guid>(spaces.Where(s => s.Visibility != SpaceVisibility.Private && !s.IsSensitive).Select(s => s.Id));
        if (!p.HasFullAccess) return [.. visible];

        if (p.IsAdmin) foreach (var s in spaces.Where(s => !s.IsSensitive)) visible.Add(s.Id);
        foreach (var a in await data.GetAssignmentsForPrincipalsAsync(PrincipalIds(p), ct)) visible.Add(a.SpaceId);
        foreach (var g in await data.GetActiveGrantsAsync(userId, time.GetUtcNow(), ct)) visible.Add(g.SpaceId);
        // A role or a grant names a space by id. Only a space that is there counts: a deleted space keeps its roles and gives nothing.
        visible.IntersectWith(spaces.Select(s => s.Id));
        return [.. visible];
    }

    private static HashSet<string> PrincipalIds(PrincipalInfo p)
    {
        var ids = new HashSet<string>(p.GroupIds, StringComparer.OrdinalIgnoreCase) { p.UserId.ToString("D") };
        return ids;
    }

}
