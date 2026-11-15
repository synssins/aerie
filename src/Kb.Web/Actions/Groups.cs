using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

// Groups. A local group is made and filled here; a directory group (AD, Entra) is brought by its plugin, which fills it from the
// directory, so Aerie changes neither its members nor its existence. Either kind gets a role in a space like a person does.

public sealed record GroupDto(Guid Id, string Key, string Name, bool IsLocal, int Members);
public sealed record GroupMemberDto(Guid UserId, string UserName, string DisplayName, string Source);
/// <summary>Someone a role can be given to. Type: "User" or "Group". Id: the principal id a grant names.</summary>
public sealed record PrincipalDto(string Type, string Id, string Name, string Detail);

[Action("group.list", Read = true)]
public sealed record ListGroups : IAction<IReadOnlyList<GroupDto>>;

[Action("group.create")]
public sealed record CreateGroup : IAction<GroupDto>
{
    public required string Name { get; init; }
}

[Action("group.rename")]
public sealed record RenameGroup : IAction<GroupDto>
{
    public required Guid GroupId { get; init; }
    public required string Name { get; init; }
}

[Action("group.delete")]
public sealed record DeleteGroup : IAction<Unit>
{
    public required Guid GroupId { get; init; }
}

[Action("group.member.list", Read = true)]
public sealed record ListGroupMembers : IAction<IReadOnlyList<GroupMemberDto>>
{
    public required Guid GroupId { get; init; }
}

[Action("group.member.add")]
public sealed record AddGroupMember : IAction<Unit>
{
    public required Guid GroupId { get; init; }
    public required Guid UserId { get; init; }
}

[Action("group.member.remove")]
public sealed record RemoveGroupMember : IAction<Unit>
{
    public required Guid GroupId { get; init; }
    public required Guid UserId { get; init; }
}

/// <summary>People and groups whose name holds the text: for an Admin, or an Owner choosing who gets a role.</summary>
[Action("principal.list", Read = true)]
public sealed record ListPrincipals : IAction<IReadOnlyList<PrincipalDto>>
{
    public string? Query { get; init; }
}

internal static class GroupRules
{
    public const int NameMax = 200;

    public static string? Clean(string? name) => string.IsNullOrWhiteSpace(name) || name.Trim().Length > NameMax ? null : name.Trim();

    /// <summary>The group, when it is local: a directory group is the directory's to change.</summary>
    public static async Task<(KbGroup? Group, Problem? Problem)> LocalAsync(KbDbContext db, Guid id, CancellationToken ct)
    {
        var g = await db.KbGroups.FirstOrDefaultAsync(x => x.Id == id, ct);
        return g is null ? (null, Problem.NotFound) : g.IsLocal ? (g, null) : (null, Problem.Forbidden);
    }

    public static async Task<GroupDto> DtoAsync(KbDbContext db, KbGroup g, CancellationToken ct) =>
        new(g.Id, g.Key, g.Name, g.IsLocal, await db.KbGroupMembers.CountAsync(m => m.GroupId == g.Id, ct));
}

internal sealed class ListGroupsHandler(KbDbContext db, IAccess access) : IActionHandler<ListGroups, IReadOnlyList<GroupDto>>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ListGroups> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<IReadOnlyList<GroupDto>>> HandleAsync(ActionContext<ListGroups> ctx)
    {
        var counts = await db.KbGroupMembers.AsNoTracking().GroupBy(m => m.GroupId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ctx.Cancel);
        var groups = await db.KbGroups.AsNoTracking().OrderBy(g => g.Name).ToListAsync(ctx.Cancel);
        return Outcome.List<GroupDto>([.. groups.Select(g => new GroupDto(g.Id, g.Key, g.Name, g.IsLocal, counts.GetValueOrDefault(g.Id)))]);
    }
}

internal sealed class CreateGroupHandler(KbDbContext db, IAccess access) : IActionHandler<CreateGroup, GroupDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<CreateGroup> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<GroupDto>> HandleAsync(ActionContext<CreateGroup> ctx)
    {
        if (GroupRules.Clean(ctx.Action.Name) is not { } name) return Problem.Invalid("name", "1-200 characters.");
        if (await db.KbGroups.AnyAsync(g => g.IsLocal && g.Name == name, ctx.Cancel)) return Problem.Conflict("A group has that name.");
        var id = Guid.NewGuid();
        var g = new KbGroup { Id = id, Key = $"local:{id:D}", Name = name, IsLocal = true };
        db.KbGroups.Add(g);
        await db.SaveChangesAsync();
        ctx.Audit(AuditActions.GroupCreate, "group", g.Id.ToString(), data: new Dictionary<string, string> { ["name"] = name });
        return new GroupDto(g.Id, g.Key, g.Name, true, 0);
    }
}

internal sealed class RenameGroupHandler(KbDbContext db, IAccess access) : IActionHandler<RenameGroup, GroupDto>
{
    private KbGroup? _group;

    public async Task<Problem?> AuthorizeAsync(ActionContext<RenameGroup> ctx)
    {
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        (_group, var problem) = await GroupRules.LocalAsync(db, ctx.Action.GroupId, ctx.Cancel);
        return problem;
    }

    public async Task<Outcome<GroupDto>> HandleAsync(ActionContext<RenameGroup> ctx)
    {
        if (GroupRules.Clean(ctx.Action.Name) is not { } name) return Problem.Invalid("name", "1-200 characters.");
        var g = _group!;
        if (await db.KbGroups.AnyAsync(x => x.IsLocal && x.Name == name && x.Id != g.Id, ctx.Cancel)) return Problem.Conflict("A group has that name.");
        var old = g.Name;
        g.Name = name;
        await db.SaveChangesAsync();
        ctx.Audit(AuditActions.GroupRename, "group", g.Id.ToString(), data: new Dictionary<string, string> { ["from"] = old, ["to"] = name });
        return await GroupRules.DtoAsync(db, g, ctx.Cancel);
    }
}

internal sealed class DeleteGroupHandler(KbDbContext db, IAccess access) : IActionHandler<DeleteGroup, Unit>
{
    private KbGroup? _group;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DeleteGroup> ctx)
    {
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        (_group, var problem) = await GroupRules.LocalAsync(db, ctx.Action.GroupId, ctx.Cancel);
        return problem;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<DeleteGroup> ctx)
    {
        var g = _group!;
        // The group's roles go with it, so no row is left giving a role to a group that is not there.
        db.SpaceRoleAssignments.RemoveRange(await db.SpaceRoleAssignments.Where(a => a.PrincipalType == Kb.Plugins.PrincipalType.Group && a.PrincipalId == g.Key).ToListAsync(ctx.Cancel));
        db.KbGroupMembers.RemoveRange(await db.KbGroupMembers.Where(m => m.GroupId == g.Id).ToListAsync(ctx.Cancel));
        db.KbGroups.Remove(g);
        await db.SaveChangesAsync();
        ctx.Audit(AuditActions.GroupDelete, "group", g.Id.ToString(), data: new Dictionary<string, string> { ["name"] = g.Name });
        return new Unit();
    }
}

internal sealed class ListGroupMembersHandler(KbDbContext db, IAccess access) : IActionHandler<ListGroupMembers, IReadOnlyList<GroupMemberDto>>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ListGroupMembers> ctx)
    {
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        return await db.KbGroups.AnyAsync(g => g.Id == ctx.Action.GroupId, ctx.Cancel) ? null : Problem.NotFound;
    }

    public async Task<Outcome<IReadOnlyList<GroupMemberDto>>> HandleAsync(ActionContext<ListGroupMembers> ctx)
    {
        var id = ctx.Action.GroupId;
        return Outcome.List(await db.KbGroupMembers.AsNoTracking().Where(m => m.GroupId == id).Join(db.Users, m => m.UserId, u => u.Id, (m, u) => u)
            .Where(u => u.DeletedAt == null).OrderBy(u => u.DisplayName)
            .Select(u => new GroupMemberDto(u.Id, u.UserName!, u.DisplayName, u.Source.ToString())).ToListAsync(ctx.Cancel));
    }
}

internal sealed class AddGroupMemberHandler(KbDbContext db, IAccess access) : IActionHandler<AddGroupMember, Unit>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<AddGroupMember> ctx)
    {
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        return (await GroupRules.LocalAsync(db, ctx.Action.GroupId, ctx.Cancel)).Problem;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<AddGroupMember> ctx)
    {
        var (groupId, userId) = (ctx.Action.GroupId, ctx.Action.UserId);
        if (!await db.Users.AnyAsync(u => u.Id == userId && u.DeletedAt == null, ctx.Cancel)) return Problem.Invalid("user", "No such user.");
        if (!await db.KbGroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == userId, ctx.Cancel))
        {
            db.KbGroupMembers.Add(new KbGroupMember { GroupId = groupId, UserId = userId });
            await db.SaveChangesAsync();
        }
        ctx.Audit(AuditActions.GroupMemberAdd, "group", groupId.ToString(), data: new Dictionary<string, string> { ["user"] = userId.ToString() });
        return new Unit();
    }
}

internal sealed class RemoveGroupMemberHandler(KbDbContext db, IAccess access) : IActionHandler<RemoveGroupMember, Unit>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<RemoveGroupMember> ctx)
    {
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        return (await GroupRules.LocalAsync(db, ctx.Action.GroupId, ctx.Cancel)).Problem;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<RemoveGroupMember> ctx)
    {
        var (groupId, userId) = (ctx.Action.GroupId, ctx.Action.UserId);
        db.KbGroupMembers.RemoveRange(await db.KbGroupMembers.Where(m => m.GroupId == groupId && m.UserId == userId).ToListAsync(ctx.Cancel));
        await db.SaveChangesAsync();
        ctx.Audit(AuditActions.GroupMemberRemove, "group", groupId.ToString(), data: new Dictionary<string, string> { ["user"] = userId.ToString() });
        return new Unit();
    }
}

internal sealed class ListPrincipalsHandler(KbDbContext db, IAccess access) : IActionHandler<ListPrincipals, IReadOnlyList<PrincipalDto>>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ListPrincipals> ctx) =>
        ctx.Actor.UserId is null ? Problem.Unauthorized : await MayBrowseAsync(db, access, ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    /// <summary>Who may look people up: an Admin, and anyone who picks people somewhere (an Owner, or someone who may edit pages).</summary>
    public static async Task<bool> MayBrowseAsync(KbDbContext db, IAccess access, Actor actor, CancellationToken ct)
    {
        if (actor.UserId is not { } me) return false;
        if (await access.IsAdminAsync(actor, ct)) return true;
        // An Owner of any space, directly or through a group, picks who gets a role there.
        var ids = await db.KbGroupMembers.Where(m => m.UserId == me).Join(db.KbGroups, m => m.GroupId, g => g.Id, (m, g) => g.Key).ToListAsync(ct);
        ids.Add(me.ToString("D"));
        // Also anyone who may edit a page somewhere: they pick a page's owner, a person or a group.
        var pickers = (await db.SpaceRoleDefs.AsNoTracking().ToListAsync(ct))
            .Where(r => Kb.Core.Spaces.SpacePerms.IsOwnerGrade(r.Permissions) || Kb.Core.Spaces.SpacePerms.Has(r.Permissions, SpaceAction.CreateEdit)).Select(r => r.Id).ToList();
        return await db.SpaceRoleAssignments.AnyAsync(a => pickers.Contains(a.RoleId) && ids.Contains(a.PrincipalId), ct);
    }

    public async Task<Outcome<IReadOnlyList<PrincipalDto>>> HandleAsync(ActionContext<ListPrincipals> ctx)
    {
        var q = (ctx.Action.Query ?? "").Trim();
        if (q.Length > 100) q = q[..100];
        // Case-blind on every database: LIKE, with the wildcards in the text taken as plain characters.
        var like = "%" + q.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_").Replace("[", @"\[") + "%";
        var users = await db.Users.AsNoTracking().Where(u => u.DeletedAt == null && u.DeactivatedAt == null && !u.IsBreakGlass
                && (q == "" || EF.Functions.Like(u.DisplayName, like, "\\") || EF.Functions.Like(u.UserName!, like, "\\")))
            .OrderBy(u => u.DisplayName).Take(20).Select(u => new { u.Id, u.DisplayName, u.UserName }).ToListAsync(ctx.Cancel);
        var groups = await db.KbGroups.AsNoTracking().Where(g => q == "" || EF.Functions.Like(g.Name, like, "\\"))
            .OrderBy(g => g.Name).Take(20).Select(g => new PrincipalDto("Group", g.Key, g.Name, g.IsLocal ? "Local" : "Directory")).ToListAsync(ctx.Cancel);
        // Formatted here, not by the database: a grant names a person by the lower-case id.
        return Outcome.List<PrincipalDto>([.. groups, .. users.Select(u => new PrincipalDto("User", u.Id.ToString("D"), u.DisplayName, u.UserName!))]);
    }
}
