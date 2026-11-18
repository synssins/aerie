using Kb.Core.Access;
using Kb.Core.Audit;
using Kb.Core.Spaces;
using Kb.Data;
using Microsoft.EntityFrameworkCore;
using static Kb.Web.SpaceSupport;

namespace Kb.Web.Actions;

internal sealed class ListSpacesHandler(KbDbContext db, IAccess access) : IActionHandler<ListSpaces, IReadOnlyList<SpaceDto>>
{
    // Nothing to refuse: the answer holds only the spaces the caller has a role in.
    public Task<Problem?> AuthorizeAsync(ActionContext<ListSpaces> ctx) => Task.FromResult<Problem?>(null);

    public async Task<Outcome<IReadOnlyList<SpaceDto>>> HandleAsync(ActionContext<ListSpaces> ctx)
    {
        var ids = await access.VisibleSpacesAsync(ctx.Actor, ctx.Cancel);
        var spaces = await db.Spaces.AsNoTracking().Where(s => ids.Contains(s.Id)).OrderBy(s => s.Name).ToListAsync(ctx.Cancel);
        var list = new List<SpaceDto>();
        foreach (var s in spaces) list.Add(ToDto(s, await access.RoleAsync(ctx.Actor, s.Id, ctx.Cancel)));
        return Outcome.List(list);
    }
}

internal sealed class ReadSpaceHandler(KbDbContext db, IAccess access) : IActionHandler<ReadSpace, SpaceDto>
{
    private SpaceRole? _role;

    // 404 for both "missing" and "no access": a space's existence is not disclosed.
    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadSpace> ctx) =>
        (_role = await access.RoleAsync(ctx.Actor, ctx.Action.SpaceId, ctx.Cancel)) is null ? Problem.NotFound : null;

    public async Task<Outcome<SpaceDto>> HandleAsync(ActionContext<ReadSpace> ctx)
    {
        var id = ctx.Action.SpaceId;
        return ToDto(await db.Spaces.AsNoTracking().FirstAsync(x => x.Id == id, ctx.Cancel), _role);
    }
}

internal sealed class CreateSpaceHandler(KbDbContext db, IAccess access, TimeProvider time) : IActionHandler<CreateSpace, SpaceDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<CreateSpace> ctx) =>
        await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<SpaceDto>> HandleAsync(ActionContext<CreateSpace> ctx)
    {
        var req = ctx.Action;
        if (string.IsNullOrWhiteSpace(req.Name)) return Problem.Invalid("name", "Required.");
        var key = req.Key;
        if (string.IsNullOrWhiteSpace(key))
        {
            // Key is internal (URLs use IDs). Derive from the name and make it unique.
            var stem = NonKeyChars().Replace(req.Name.Trim().ToLowerInvariant(), "-").Trim('-');
            stem = stem.Length < 2 ? "space" : stem[..Math.Min(stem.Length, 28)].TrimEnd('-');
            key = stem;
            // A deleted space keeps its key (it can come back), so the keys of deleted spaces count as taken too.
            for (var n = 2; await db.Spaces.IgnoreQueryFilters().AnyAsync(x => x.Key == key, ctx.Cancel); n++) key = $"{stem}-{n}";
        }
        else if (!SpaceKeyPattern().IsMatch(key)) return Problem.Invalid("key", "2-32 chars: lowercase letters, digits, hyphen.");
        else if (await db.Spaces.IgnoreQueryFilters().AnyAsync(x => x.Key == key, ctx.Cancel)) return new Problem(409);
        if (!Enum.IsDefined(req.Visibility)) return Problem.Invalid("visibility", "Private, SignedIn or Anyone.");
        if (req.IsSensitive && req.Visibility != SpaceVisibility.Private) return SensitiveStaysPrivate;
        var s = new Space { Key = key, Name = req.Name.Trim(), Description = req.Description, IsSensitive = req.IsSensitive, Visibility = req.Visibility, CreatedAt = time.GetUtcNow() };
        db.Spaces.Add(s);
        await db.SaveChangesAsync();
        ctx.Audit(AuditActions.SpaceCreate, "space", s.Id.ToString(), s.Id);
        return ToDto(s, SpaceRole.Owner);
    }
}

internal sealed class UpdateSpaceHandler(KbDbContext db, IAccess access) : IActionHandler<UpdateSpace, SpaceDto>
{
    private Space? _space;

    public async Task<Problem?> AuthorizeAsync(ActionContext<UpdateSpace> ctx)
    {
        var id = ctx.Action.SpaceId;
        if (await ManagesAsync(access, ctx.Actor, id, ctx.Cancel) is { } denied) return denied;
        _space = await db.Spaces.FirstAsync(x => x.Id == id, ctx.Cancel);
        if (NeedsAdmin(_space, ctx.Action) && !await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        return ctx.Actor.UserId is null ? Problem.Unauthorized : null; // the audit entry names who changed the space
    }

    public async Task<Outcome<SpaceDto>> HandleAsync(ActionContext<UpdateSpace> ctx)
    {
        var s = _space!;
        if (await UpdateSpaceAsync(s, ctx.Action, db, ctx) is { } problem) return problem;
        return ToDto(s, await access.RoleAsync(ctx.Actor, s.Id)); // read again: the update may have changed the caller's role
    }
}

internal sealed class UpdateSpaceAdminHandler(KbDbContext db, IAccess access) : IActionHandler<UpdateSpaceAdmin, SpaceDto>
{
    private Space? _space;

    public async Task<Problem?> AuthorizeAsync(ActionContext<UpdateSpaceAdmin> ctx)
    {
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        var id = ctx.Action.SpaceId;
        if ((_space = await db.Spaces.FirstOrDefaultAsync(x => x.Id == id, ctx.Cancel)) is null) return Problem.NotFound;
        // "Admins have no silent access to a sensitive space." Clearing its flag makes every Admin its Owner: an Admin does it only with
        // access to the space (a role, or emergency access, which tells the Owners). A sensitive space cannot be opened at all (it stays Private).
        return Lowers(_space, ctx.Action) && await access.RoleAsync(ctx.Actor, id, ctx.Cancel) is null
            ? new Problem(403, "No access to this space.") : null;
    }

    /// <summary>True when the update takes protection from a sensitive space: the flag is cleared.</summary>
    private static bool Lowers(Space s, UpdateSpaceAdmin req) => s.IsSensitive && req.IsSensitive == false;

    public async Task<Outcome<SpaceDto>> HandleAsync(ActionContext<UpdateSpaceAdmin> ctx)
    {
        var (req, s) = (ctx.Action, _space!);
        // The same update as an Owner's. An Admin needs no role in the space for it, except to lower a sensitive space's protection (above).
        var update = new UpdateSpace { SpaceId = req.SpaceId, Name = req.Name, Description = req.Description, IsSensitive = req.IsSensitive, Visibility = req.Visibility,
            CommentsEnabled = req.CommentsEnabled, RatingsEnabled = req.RatingsEnabled, AgingDays = req.AgingDays, AgingAudience = req.AgingAudience, RecycleDays = req.RecycleDays, ReviewMonths = req.ReviewMonths };
        if (await UpdateSpaceAsync(s, update, db, ctx) is { } problem) return problem;
        // What the Owners wrote about a sensitive space is not handed back to an Admin without access to it (as in the list of all spaces).
        return s.IsSensitive && await access.RoleAsync(ctx.Actor, s.Id, ctx.Cancel) is null ? ToDto(s, null) with { Description = null } : ToDto(s, null);
    }
}

// Deleting and restoring a space are an Admin's, whatever role anyone has in it. AuthorizeAsync answers 403 to everyone else
// before the phrase is looked at.

internal sealed class DeleteSpaceHandler(KbDbContext db, IAccess access, TimeProvider time, Kb.Core.Access.IAccessData owners, Kb.Core.Notifications.INotifier notifier)
    : IActionHandler<DeleteSpace, Unit>
{
    private Space? _space;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DeleteSpace> ctx)
    {
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        var id = ctx.Action.SpaceId;
        return (_space = await db.Spaces.FirstOrDefaultAsync(x => x.Id == id, ctx.Cancel)) is null ? Problem.NotFound : null; // a deleted space is not there to delete
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<DeleteSpace> ctx)
    {
        var s = _space!;
        // The phrase is asked for here, on the server. What a screen asked before it sent this is not the control.
        var phrase = $"delete {s.Key}";
        if (!string.Equals(ctx.Action.Confirm?.Trim(), phrase, StringComparison.OrdinalIgnoreCase)) return Problem.Invalid("confirm", $"Type: {phrase}");

        var pages = await db.Pages.CountAsync(p => p.SpaceId == s.Id); // system: how much goes with it, for the audit entry
        var told = await owners.GetOwnerUserIdsAsync(s.Id, CancellationToken.None);
        (s.DeletedAt, s.DeletedBy) = (time.GetUtcNow(), ctx.Actor.UserId);
        await db.SaveChangesAsync();
        ctx.Audit(AuditActions.SpaceDelete, "space", s.Id.ToString(), s.Id,
            new Dictionary<string, string> { ["key"] = s.Key, ["name"] = s.Name, ["pages"] = pages.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        // Its Owners lose the space with everyone else: they are told, as for every act of an Admin on their space.
        await notifier.NotifyAsync(told, Kb.Core.Notifications.NotificationKinds.SecurityAlert, $"{s.Name} was deleted",
            "An administrator moved the space to the recycle bin. An administrator can restore it.", null, alsoEmail: true, CancellationToken.None);
        return new Unit();
    }
}

internal sealed class RestoreSpaceHandler(KbDbContext db, IAccess access, Kb.Core.Access.IAccessData owners, Kb.Core.Notifications.INotifier notifier) : IActionHandler<RestoreSpace, SpaceDto>
{
    private Space? _space;

    public async Task<Problem?> AuthorizeAsync(ActionContext<RestoreSpace> ctx)
    {
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        var id = ctx.Action.SpaceId;
        _space = await db.Spaces.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt != null, ctx.Cancel); // system: only a deleted space can be restored, and only an Admin gets here
        return _space is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<SpaceDto>> HandleAsync(ActionContext<RestoreSpace> ctx)
    {
        var s = _space!;
        (s.DeletedAt, s.DeletedBy) = (null, null);
        await db.SaveChangesAsync();
        ctx.Audit(AuditActions.SpaceRestore, "space", s.Id.ToString(), s.Id, new Dictionary<string, string> { ["key"] = s.Key, ["name"] = s.Name });
        await notifier.NotifyAsync(await owners.GetOwnerUserIdsAsync(s.Id, CancellationToken.None), Kb.Core.Notifications.NotificationKinds.SecurityAlert,
            $"{s.Name} was restored", "An administrator took the space out of the recycle bin.", $"/s/{s.Id}", alsoEmail: false, CancellationToken.None);
        // The space is as it was: the roles in it are again what they were. The Admin's own role is read like anyone's.
        return ToDto(s, await access.RoleAsync(ctx.Actor, s.Id));
    }
}

internal sealed class ListDeletedSpacesHandler(KbDbContext db, IAccess access) : IActionHandler<ListDeletedSpaces, IReadOnlyList<DeletedSpaceDto>>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ListDeletedSpaces> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<IReadOnlyList<DeletedSpaceDto>>> HandleAsync(ActionContext<ListDeletedSpaces> ctx)
    {
        // system: the recycle bin of spaces. Names only, as an Admin sees the name of every space; nothing of their content.
        var spaces = await db.Spaces.IgnoreQueryFilters().AsNoTracking().Where(x => x.DeletedAt != null).OrderByDescending(x => x.DeletedAt).ToListAsync(ctx.Cancel);
        // A sensitive space says nothing of what it holds, not even how much.
        var ids = spaces.Where(x => !x.IsSensitive).Select(x => x.Id).ToList();
        var counts = await db.Pages.Where(p => ids.Contains(p.SpaceId)).GroupBy(p => p.SpaceId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.N, ctx.Cancel); // system: a count, no content
        var by = spaces.Where(x => x.DeletedBy != null).Select(x => x.DeletedBy!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => by.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ctx.Cancel);
        return Outcome.List<DeletedSpaceDto>([.. spaces.Select(x => new DeletedSpaceDto(x.Id, x.Key, x.Name, x.IsSensitive, x.DeletedAt!.Value,
            x.DeletedBy is { } u && names.TryGetValue(u, out var name) ? name : "Unknown", x.IsSensitive ? null : counts.GetValueOrDefault(x.Id)))]);
    }
}

internal sealed class ListSpaceRolesHandler(KbDbContext db, IAccess access) : IActionHandler<ListSpaceRoles, IReadOnlyList<RoleAssignmentDto>>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<ListSpaceRoles> ctx) => ManagesAsync(access, ctx.Actor, ctx.Action.SpaceId, ctx.Cancel);

    public async Task<Outcome<IReadOnlyList<RoleAssignmentDto>>> HandleAsync(ActionContext<ListSpaceRoles> ctx)
    {
        var id = ctx.Action.SpaceId;
        var rows = await db.SpaceRoleAssignments.AsNoTracking().Where(a => a.SpaceId == id).ToListAsync(ctx.Cancel);
        var userIds = rows.Where(a => a.PrincipalType == PrincipalType.User).Select(a => Guid.TryParse(a.PrincipalId, out var g) ? g : Guid.Empty).ToList();
        var groupKeys = rows.Where(a => a.PrincipalType == PrincipalType.Group).Select(a => a.PrincipalId).ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id.ToString("D"), u => u.DisplayName, ctx.Cancel);
        var groups = await db.KbGroups.AsNoTracking().Where(g => groupKeys.Contains(g.Key)).ToDictionaryAsync(g => g.Key, g => g.Name, ctx.Cancel);
        var roles = await db.SpaceRoleDefs.AsNoTracking().ToListAsync(ctx.Cancel);
        var byId = roles.ToDictionary(r => r.Id);
        SpaceRole Level(SpaceRoleAssignment a) => SpacePerms.Rank(byId.TryGetValue(a.RoleId, out var r) ? r.Permissions : 0, roles) ?? SpaceRole.Reader;
        return Outcome.List<RoleAssignmentDto>([.. rows.OrderByDescending(Level).ThenBy(a => byId.GetValueOrDefault(a.RoleId)?.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => new RoleAssignmentDto(a.Id, a.PrincipalType, a.PrincipalId, Level(a),
                (a.PrincipalType == PrincipalType.User ? users : groups).GetValueOrDefault(a.PrincipalId), a.RoleId, byId.GetValueOrDefault(a.RoleId)?.Name))]);
    }
}

internal sealed class GrantSpaceRoleHandler(KbDbContext db, IAccess access) : IActionHandler<GrantSpaceRole, Unit>
{
    private SpaceRoleAssignment? _existing;
    private SpaceRoleDef? _role;

    public async Task<Problem?> AuthorizeAsync(ActionContext<GrantSpaceRole> ctx)
    {
        var req = ctx.Action;
        if (await ManagesAsync(access, ctx.Actor, req.SpaceId, ctx.Cancel) is { } denied) return denied;
        var principal = Canonical(req.PrincipalType, req.PrincipalId);
        _existing = await db.SpaceRoleAssignments.FirstOrDefaultAsync(a => a.SpaceId == req.SpaceId && a.PrincipalType == req.PrincipalType && a.PrincipalId == principal, ctx.Cancel);
        var roleId = req.RoleId ?? (req.Role is { } level && Enum.IsDefined(level) ? SpacePerms.IdOf(level) : Guid.Empty);
        _role = await db.SpaceRoleDefs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roleId, ctx.Cancel);
        var before = _existing is null ? null : await db.SpaceRoleDefs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == _existing.RoleId, ctx.Cancel);
        // An Admin makes Owners, and only an Admin changes an Owner row: an Owner cannot take its own role or another Owner's.
        // An Owner is anyone whose role manages members and settings, whatever the role is called.
        // A sensitive space is the exception for making one: Admins have no access there, so its Owners add an Owner themselves.
        if ((SpacePerms.IsOwnerGrade(_role?.Permissions ?? 0) || SpacePerms.IsOwnerGrade(before?.Permissions ?? 0)) && !await access.IsAdminAsync(ctx.Actor, ctx.Cancel)
            && !(_existing is null && await db.Spaces.AnyAsync(s => s.Id == req.SpaceId && s.IsSensitive, ctx.Cancel)))
            return Problem.Forbidden;
        return ctx.Actor.UserId is null ? Problem.Unauthorized : null; // the audit entry names who granted the role
    }

    /// <summary>A user's id in the one form it is stored in, however it was typed: a second spelling would be a second row for the same person.</summary>
    private static string Canonical(PrincipalType type, string? id) => type == PrincipalType.User && Guid.TryParse(id, out var g) ? g.ToString("D") : id ?? "";

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<GrantSpaceRole> ctx)
    {
        var req = ctx.Action;
        if (_role is null) return Problem.Invalid("role", "No such role.");
        // A user is named by id. A name there would be a role nobody holds, and a row the Owners' list cannot read.
        if (string.IsNullOrWhiteSpace(req.PrincipalId) || (req.PrincipalType == PrincipalType.User && !Guid.TryParse(req.PrincipalId, out _)))
            return Problem.Invalid("principal", "Invalid.");
        if (req.PrincipalType == PrincipalType.Group && !await db.KbGroups.AnyAsync(g => g.Key == req.PrincipalId, ctx.Cancel))
            return Problem.Invalid("principal", "No such group.");
        // A role is held by an account that exists and is in use; an id that names nobody would be a row the Owners' list shows without a name.
        if (req.PrincipalType == PrincipalType.User && Guid.Parse(req.PrincipalId) is var uid && !await db.Users.AnyAsync(u => u.Id == uid && u.DeletedAt == null && u.DeactivatedAt == null, ctx.Cancel))
            return Problem.Invalid("principal", "No such user.");
        // A grant sets the role, so lowering one is a grant too. One row per person or group in a space.
        if (_existing is null) db.SpaceRoleAssignments.Add(new SpaceRoleAssignment { SpaceId = req.SpaceId, PrincipalType = req.PrincipalType, PrincipalId = Canonical(req.PrincipalType, req.PrincipalId), RoleId = _role.Id });
        else _existing.RoleId = _role.Id;
        await db.SaveChangesAsync();
        ctx.Audit(AuditActions.SpaceRoleGrant, req.PrincipalType.ToString(), req.PrincipalId, req.SpaceId, new Dictionary<string, string> { ["role"] = _role.Name, ["roleId"] = _role.Id.ToString() });
        return new Unit();
    }
}

internal sealed class RevokeSpaceRoleHandler(KbDbContext db, IAccess access) : IActionHandler<RevokeSpaceRole, Unit>
{
    private SpaceRoleAssignment? _assignment;

    public async Task<Problem?> AuthorizeAsync(ActionContext<RevokeSpaceRole> ctx)
    {
        var (id, assignmentId) = (ctx.Action.SpaceId, ctx.Action.AssignmentId);
        if (await ManagesAsync(access, ctx.Actor, id, ctx.Cancel) is { } denied) return denied;
        if ((_assignment = await db.SpaceRoleAssignments.FirstOrDefaultAsync(x => x.Id == assignmentId && x.SpaceId == id, ctx.Cancel)) is null) return Problem.NotFound;
        var roleId = _assignment.RoleId;
        // Owners (anyone whose role manages members and settings) are removed by an Admin.
        if (SpacePerms.IsOwnerGrade(await db.SpaceRoleDefs.Where(r => r.Id == roleId).Select(r => r.Permissions).FirstOrDefaultAsync(ctx.Cancel))
            && !await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        return ctx.Actor.UserId is null ? Problem.Unauthorized : null; // the audit entry names who revoked the role
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<RevokeSpaceRole> ctx)
    {
        var a = _assignment!;
        db.SpaceRoleAssignments.Remove(a);
        await db.SaveChangesAsync();
        ctx.Audit(AuditActions.SpaceRoleRevoke, a.PrincipalType.ToString(), a.PrincipalId, ctx.Action.SpaceId);
        return new Unit();
    }
}

internal sealed class ListAllSpacesHandler(KbDbContext db, IAccess access) : IActionHandler<ListAllSpaces, IReadOnlyList<SpaceDto>>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ListAllSpaces> ctx) =>
        await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<IReadOnlyList<SpaceDto>>> HandleAsync(ActionContext<ListAllSpaces> ctx) =>
        Outcome.List<SpaceDto>([.. await Task.WhenAll((await db.Spaces.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ctx.Cancel)).Select(async x =>
            // A sensitive space is listed, so an Admin knows it exists and can ask for emergency access; what its Owners wrote about it is not.
            x.IsSensitive && await access.RoleAsync(ctx.Actor, x.Id, ctx.Cancel) is null ? ToDto(x, null) with { Description = null } : ToDto(x, null)))]);
}
