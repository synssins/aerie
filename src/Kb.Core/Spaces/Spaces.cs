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

public sealed class Space
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Key { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool IsSensitive { get; set; }
    public SpaceVisibility Visibility { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool CommentsEnabled { get; set; } = true;
    public int AgingDays { get; set; } = 180;
    public AgingAudience AgingAudience { get; set; } = AgingAudience.Everyone;
    /// <summary>Recycle-bin retention override; null uses Kb:Retention:RecycleDays.</summary>
    public int? RecycleDays { get; set; }
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
    public SpaceRole Role { get; set; }
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
    Task<IReadOnlyList<SpaceRoleAssignment>> GetAssignmentsForPrincipalsAsync(IReadOnlyCollection<string> userAndGroupIds, CancellationToken ct);
    Task<IReadOnlyList<SpaceAccessGrant>> GetActiveGrantsAsync(Guid userId, DateTimeOffset now, CancellationToken ct);
}

public interface ISpacePermissionService
{
    Task<SpaceRole?> GetRoleAsync(Guid userId, Guid spaceId, CancellationToken ct = default);
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
    public async Task<SpaceRole?> GetRoleAsync(Guid userId, Guid spaceId, CancellationToken ct = default)
    {
        var p = await data.GetPrincipalAsync(userId, ct);
        var space = await data.GetSpaceAsync(spaceId, ct);
        if (p is null || space is null) return null;
        // A sensitive space is always Private; the check here holds even if a row says otherwise.
        var publicOn = space.Visibility != SpaceVisibility.Private && !space.IsSensitive;

        if (!p.HasFullAccess) return publicOn ? SpaceRole.Reader : null;

        SpaceRole? best = publicOn ? SpaceRole.Reader : null;
        var ids = PrincipalIds(p);
        foreach (var a in await data.GetAssignmentsAsync(spaceId, ct))
            if (ids.Contains(a.PrincipalId)) best = Max(best, a.Role);

        if (p.IsAdmin && !space.IsSensitive) best = SpaceRole.Owner;

        foreach (var g in await data.GetActiveGrantsAsync(userId, time.GetUtcNow(), ct))
            if (g.SpaceId == spaceId) best = Max(best, g.Role);

        return best;
    }

    public async Task<bool> CanAsync(Guid userId, Guid spaceId, SpaceAction action, CancellationToken ct = default)
    {
        var role = await GetRoleAsync(userId, spaceId, ct);
        return role is { } r && r >= RoleMatrix.MinimumRole(action);
    }

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

    private static SpaceRole? Max(SpaceRole? a, SpaceRole b) => a is null || b > a ? b : a;
}
