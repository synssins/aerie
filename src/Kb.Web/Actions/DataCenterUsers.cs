using Kb.Core.Identity;
using Kb.Data;
using Kb.Web.Actions;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.DataCenter;

// Users on the Data Center compatible surface (contracts/behaviour/users.md). A user's key is aerie's user id as 32 lower-case hex digits.
// Who may read whom follows aerie's own rule for looking people up (ListPrincipalsHandler.MayBrowseAsync);
// see docs/spec-questions/user-directory-visibility.md.

public sealed record DcUser(Guid Id, string UserName, string DisplayName, bool Deactivated, bool Deleted);
public sealed record DcUserPage(IReadOnlyList<DcUser> Users, bool More);
public sealed record DcGroupPage(IReadOnlyList<string> Names, bool More);

[Action("dc.user.read", Read = true)]
public sealed record DcReadUser : IAction<DcUser>
{
    public string? Key { get; init; }
    public string? UserName { get; init; }
}

[Action("dc.user.list", Read = true)]
public sealed record DcListUsers : IAction<DcUserPage>
{
    public required int Start { get; init; }
    public required int Limit { get; init; }
}

[Action("dc.user.groups", Read = true)]
public sealed record DcUserGroups : IAction<DcGroupPage>
{
    public string? Key { get; init; }
    public string? UserName { get; init; }
    public required int Start { get; init; }
    public required int Limit { get; init; }
}

internal static class DcUserData
{
    public static DcUser View(KbUser u) => new(u.Id, u.UserName ?? "", u.DisplayName, u.DeactivatedAt is not null, u.DeletedAt is not null);

    /// <summary>Exactly one of key and user name, else 400. A deleted account or the break-glass account is not found.</summary>
    public static async Task<(KbUser? User, Problem? Problem)> FindAsync(KbDbContext db, string? key, string? userName, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(key) == string.IsNullOrEmpty(userName)) return (null, new Problem(400, "Give key or username, not both."));
        var users = db.Users.AsNoTracking().Where(u => u.DeletedAt == null && !u.IsBreakGlass);
        KbUser? user;
        if (!string.IsNullOrEmpty(key))
            user = key.Length == 32 && Guid.TryParseExact(key, "N", out var id) ? await users.FirstOrDefaultAsync(u => u.Id == id, ct) : null;
        else
        {
            var normalized = userName!.ToUpperInvariant();
            user = await users.FirstOrDefaultAsync(u => u.NormalizedUserName == normalized, ct);
        }
        return user is null ? (null, new Problem(404, "No such user.")) : (user, null);
    }
}

internal sealed class DcReadUserHandler(KbDbContext db, IAccess access) : IActionHandler<DcReadUser, DcUser>
{
    private KbUser? _user;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcReadUser> ctx)
    {
        // The site does not show profiles to someone not signed in (users.md: 403).
        if (ctx.Actor.UserId is not { } me) return new Problem(403, "Profiles are not shown to anonymous users.");
        var (user, problem) = await DcUserData.FindAsync(db, ctx.Action.Key, ctx.Action.UserName, ctx.Cancel);
        if (user?.Id == me) { _user = user; return null; }
        if (!await ListPrincipalsHandler.MayBrowseAsync(db, access, ctx.Actor, ctx.Cancel)) return new Problem(403, "You may not look up users.");
        _user = user;
        return problem;
    }

    public Task<Outcome<DcUser>> HandleAsync(ActionContext<DcReadUser> ctx) => Task.FromResult<Outcome<DcUser>>(DcUserData.View(_user!));
}

internal sealed class DcListUsersHandler(KbDbContext db, IAccess access) : IActionHandler<DcListUsers, DcUserPage>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<DcListUsers> ctx) =>
        ctx.Actor.UserId is null ? Problem.Unauthorized
        : await ListPrincipalsHandler.MayBrowseAsync(db, access, ctx.Actor, ctx.Cancel) ? null : new Problem(403, "You may not list users.");

    public async Task<Outcome<DcUserPage>> HandleAsync(ActionContext<DcListUsers> ctx)
    {
        var (start, limit) = (ctx.Action.Start, ctx.Action.Limit);
        // Every account, enabled or not; a deleted account is no longer one, and the break-glass account is not listed anywhere.
        var rows = await db.Users.AsNoTracking().Where(u => u.DeletedAt == null && !u.IsBreakGlass).OrderBy(u => u.NormalizedUserName)
            .Skip(start).Take(limit + 1).ToListAsync(ctx.Cancel);
        return new DcUserPage([.. rows.Take(limit).Select(DcUserData.View)], rows.Count > limit);
    }
}

internal sealed class DcUserGroupsHandler(KbDbContext db, IAccess access) : IActionHandler<DcUserGroups, DcGroupPage>
{
    private KbUser? _user;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcUserGroups> ctx)
    {
        if (ctx.Actor.UserId is not { } me) return Problem.Unauthorized;
        var admin = await access.IsAdminAsync(ctx.Actor, ctx.Cancel);
        var (user, problem) = await DcUserData.FindAsync(db, ctx.Action.Key, ctx.Action.UserName, ctx.Cancel);
        // Group memberships are an Admin's to see; anyone sees their own.
        if (!admin && user?.Id != me) return new Problem(403, "You may not see this user's groups.");
        _user = user;
        return problem;
    }

    public async Task<Outcome<DcGroupPage>> HandleAsync(ActionContext<DcUserGroups> ctx)
    {
        var (start, limit, id) = (ctx.Action.Start, ctx.Action.Limit, _user!.Id);
        var names = await db.KbGroupMembers.AsNoTracking().Where(m => m.UserId == id).Join(db.KbGroups, m => m.GroupId, g => g.Id, (m, g) => g.Name)
            .OrderBy(n => n).Skip(start).Take(limit + 1).ToListAsync(ctx.Cancel);
        return new DcGroupPage([.. names.Take(limit)], names.Count > limit);
    }
}
