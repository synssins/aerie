using Kb.Core.Audit;
using Kb.Core.Spaces;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

// Roles: one global list of named sets of the ten space permissions, used in every space. Reader, Contributor, Supervisor and
// Owner are built in: an Admin renames and edits them but never deletes one. Every change is an Admin's and is audited.

/// <summary>Permissions: names of <see cref="SpaceAction"/>. BuiltIn: the level a built-in role is, null for one an Admin added. InUse: assignments.</summary>
public sealed record RoleDto(Guid Id, string Name, IReadOnlyList<string> Permissions, string? BuiltIn, int InUse);
public sealed record RoleRequest(string? Name, IReadOnlyList<string>? Permissions);

/// <summary>Every role. For anyone signed in: an Owner picks from it on a space's Members tab.</summary>
[Action("role.list", Read = true)]
public sealed record ListRoles : IAction<IReadOnlyList<RoleDto>>;

[Action("role.create")]
public sealed record CreateRole : IAction<RoleDto>
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Permissions { get; init; }
}

[Action("role.update")]
public sealed record UpdateRole : IAction<RoleDto>
{
    public required Guid RoleId { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<string> Permissions { get; init; }
}

/// <summary>Refused for a built-in role and for one in use (the answer gives the count).</summary>
[Action("role.delete")]
public sealed record DeleteRole : IAction<Unit>
{
    public required Guid RoleId { get; init; }
}

internal static class RoleSupport
{
    internal static RoleDto ToDto(SpaceRoleDef r, int inUse) => new(r.Id, r.Name, [.. SpacePerms.Actions(r.Permissions).Select(a => a.ToString())], r.BuiltIn?.ToString(), inUse);

    /// <summary>The name and the permissions, checked. Every role reads: without Read it would show a space and nothing in it.</summary>
    internal static async Task<(string Name, int Mask, Problem? Problem)> CheckAsync(KbDbContext db, string? name, IReadOnlyList<string>? permissions, Guid? self, CancellationToken ct)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length is 0 or > 50) return ("", 0, Problem.Invalid("name", "1-50 characters."));
        var all = await db.SpaceRoleDefs.Where(r => r.Id != self).Select(r => r.Name).ToListAsync(ct);
        if (all.Any(n => string.Equals(n, trimmed, StringComparison.OrdinalIgnoreCase))) return ("", 0, Problem.Invalid("name", "Name in use."));
        var mask = 0;
        foreach (var p in permissions ?? [])
        {
            if (!Enum.TryParse<SpaceAction>(p, out var action) || !Enum.IsDefined(action) || int.TryParse(p, out _)) return ("", 0, Problem.Invalid("permissions", $"Unknown permission: {p}."));
            mask |= SpacePerms.Of(action);
        }
        if (!SpacePerms.Has(mask, SpaceAction.Read)) return ("", 0, Problem.Invalid("permissions", "Read is required."));
        return (trimmed, mask, null);
    }

    internal static Dictionary<string, string> Data(string name, int mask) =>
        new() { ["name"] = name, ["permissions"] = string.Join(",", SpacePerms.Actions(mask)) };
}

internal sealed class ListRolesHandler(KbDbContext db) : IActionHandler<ListRoles, IReadOnlyList<RoleDto>>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<ListRoles> ctx) => Task.FromResult(ctx.Actor.UserId is null ? Problem.Unauthorized : null);

    public async Task<Outcome<IReadOnlyList<RoleDto>>> HandleAsync(ActionContext<ListRoles> ctx)
    {
        var roles = await db.SpaceRoleDefs.AsNoTracking().ToListAsync(ctx.Cancel);
        var used = await db.SpaceRoleAssignments.AsNoTracking().GroupBy(a => a.RoleId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ctx.Cancel);
        // The built-in ones first, weakest first, then the others by name.
        return Outcome.List<RoleDto>([.. roles.OrderBy(r => r.BuiltIn is null).ThenBy(r => r.BuiltIn).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => RoleSupport.ToDto(r, used.GetValueOrDefault(r.Id)))]);
    }
}

internal sealed class CreateRoleHandler(KbDbContext db, IAccess access) : IActionHandler<CreateRole, RoleDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<CreateRole> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<RoleDto>> HandleAsync(ActionContext<CreateRole> ctx)
    {
        var (name, mask, problem) = await RoleSupport.CheckAsync(db, ctx.Action.Name, ctx.Action.Permissions, null, ctx.Cancel);
        if (problem is not null) return problem;
        var role = new SpaceRoleDef { Name = name, Permissions = mask };
        db.SpaceRoleDefs.Add(role);
        await db.SaveChangesAsync(ctx.Cancel);
        ctx.Audit(AuditActions.RoleCreate, "role", role.Id.ToString(), data: RoleSupport.Data(name, mask));
        return RoleSupport.ToDto(role, 0);
    }
}

internal sealed class UpdateRoleHandler(KbDbContext db, IAccess access) : IActionHandler<UpdateRole, RoleDto>
{
    private SpaceRoleDef? _role;

    public async Task<Problem?> AuthorizeAsync(ActionContext<UpdateRole> ctx)
    {
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        return (_role = await db.SpaceRoleDefs.FirstOrDefaultAsync(r => r.Id == ctx.Action.RoleId, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<RoleDto>> HandleAsync(ActionContext<UpdateRole> ctx)
    {
        var role = _role!;
        var (name, mask, problem) = await RoleSupport.CheckAsync(db, ctx.Action.Name, ctx.Action.Permissions, role.Id, ctx.Cancel);
        if (problem is not null) return problem;
        (role.Name, role.Permissions) = (name, mask);
        await db.SaveChangesAsync(ctx.Cancel);
        ctx.Audit(AuditActions.RoleUpdate, "role", role.Id.ToString(), data: RoleSupport.Data(name, mask));
        return RoleSupport.ToDto(role, await db.SpaceRoleAssignments.CountAsync(a => a.RoleId == role.Id, ctx.Cancel));
    }
}

internal sealed class DeleteRoleHandler(KbDbContext db, IAccess access) : IActionHandler<DeleteRole, Unit>
{
    private SpaceRoleDef? _role;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DeleteRole> ctx)
    {
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        return (_role = await db.SpaceRoleDefs.FirstOrDefaultAsync(r => r.Id == ctx.Action.RoleId, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<DeleteRole> ctx)
    {
        var role = _role!;
        if (role.BuiltIn is not null) return Problem.Conflict("A built-in role is not deleted.");
        // A role in use stays: removing it would take people's access away without anyone choosing that, space by space.
        var used = await db.SpaceRoleAssignments.CountAsync(a => a.RoleId == role.Id, ctx.Cancel);
        if (used > 0) return Problem.Conflict(used == 1 ? "Used in 1 place." : $"Used in {used} places.");
        db.SpaceRoleDefs.Remove(role);
        await db.SaveChangesAsync(ctx.Cancel);
        ctx.Audit(AuditActions.RoleDelete, "role", role.Id.ToString(), data: new Dictionary<string, string> { ["name"] = role.Name });
        return new Unit();
    }
}
