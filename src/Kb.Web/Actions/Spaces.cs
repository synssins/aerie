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
            for (var n = 2; await db.Spaces.AnyAsync(x => x.Key == key, ctx.Cancel); n++) key = $"{stem}-{n}";
        }
        else if (!SpaceKeyPattern().IsMatch(key)) return Problem.Invalid("key", "2-32 chars: lowercase letters, digits, hyphen.");
        else if (await db.Spaces.AnyAsync(x => x.Key == key, ctx.Cancel)) return new Problem(409);
        var s = new Space { Key = key, Name = req.Name.Trim(), Description = req.Description, IsSensitive = req.IsSensitive, IsPublic = req.IsPublic, CreatedAt = time.GetUtcNow() };
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
        return (_space = await db.Spaces.FirstOrDefaultAsync(x => x.Id == id, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<SpaceDto>> HandleAsync(ActionContext<UpdateSpaceAdmin> ctx)
    {
        var (req, s) = (ctx.Action, _space!);
        // The same update as an Owner's; an Admin needs no role in the space, so none is checked and none is answered.
        var update = new UpdateSpace { SpaceId = req.SpaceId, Name = req.Name, Description = req.Description, IsSensitive = req.IsSensitive, IsPublic = req.IsPublic,
            CommentsEnabled = req.CommentsEnabled, AgingDays = req.AgingDays, AgingAudience = req.AgingAudience, RecycleDays = req.RecycleDays };
        if (await UpdateSpaceAsync(s, update, db, ctx) is { } problem) return problem;
        return ToDto(s, null);
    }
}

internal sealed class ListSpaceRolesHandler(KbDbContext db, IAccess access) : IActionHandler<ListSpaceRoles, IReadOnlyList<RoleAssignmentDto>>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<ListSpaceRoles> ctx) => ManagesAsync(access, ctx.Actor, ctx.Action.SpaceId, ctx.Cancel);

    public async Task<Outcome<IReadOnlyList<RoleAssignmentDto>>> HandleAsync(ActionContext<ListSpaceRoles> ctx)
    {
        var id = ctx.Action.SpaceId;
        return Outcome.List(await db.SpaceRoleAssignments.AsNoTracking().Where(a => a.SpaceId == id)
            .Select(a => new RoleAssignmentDto(a.Id, a.PrincipalType, a.PrincipalId, a.Role)).ToListAsync(ctx.Cancel));
    }
}

internal sealed class GrantSpaceRoleHandler(IAccess access, IAccessData data) : IActionHandler<GrantSpaceRole, Unit>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<GrantSpaceRole> ctx)
    {
        if (await ManagesAsync(access, ctx.Actor, ctx.Action.SpaceId, ctx.Cancel) is { } denied) return denied;
        return ctx.Actor.UserId is null ? Problem.Unauthorized : null; // the audit entry names who granted the role
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<GrantSpaceRole> ctx)
    {
        var req = ctx.Action;
        if (string.IsNullOrWhiteSpace(req.PrincipalId) || !Enum.IsDefined(req.Role)) return Problem.Invalid("principal", "Invalid.");
        await data.AddAssignmentAsync(new SpaceRoleAssignment { SpaceId = req.SpaceId, PrincipalType = req.PrincipalType, PrincipalId = req.PrincipalId, Role = req.Role }, default);
        ctx.Audit(AuditActions.SpaceRoleGrant, req.PrincipalType.ToString(), req.PrincipalId, req.SpaceId, new Dictionary<string, string> { ["role"] = req.Role.ToString() });
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
        Outcome.List<SpaceDto>([.. (await db.Spaces.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ctx.Cancel)).Select(x => ToDto(x, null))]);
}
