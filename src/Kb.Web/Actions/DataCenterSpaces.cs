using Kb.Core.Audit;
using Kb.Core.DataCenter;
using Kb.Core.Identity;
using Kb.Core.Spaces;
using Kb.Data;
using Kb.Web.DataCenter;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

// Spaces and long tasks on the Data Center compatible surface (contracts/behaviour/spaces.md, common.md). Each action finds the space by its key,
// lower-cased (spaces.md, Space keys), and decides access through IAccess. Writes are aerie's own actions, sent from inside, so their checks,
// plugin hooks and audit entries apply as they do for /api/v1.

/// <summary>A space as the surface shows it.</summary>
public sealed record DcSpaceView(Guid Id, long DcId, string Key, string Name, string? Description, bool Personal, bool Archived, DcUser? Creator, DateTimeOffset CreatedAt);
public sealed record DcSpacePage(IReadOnlyList<DcSpaceView> Spaces, bool More);
public sealed record DcLongTaskView(Guid Id, string Name, string Subject, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, bool Successful);
public sealed record DcLongTaskPage(IReadOnlyList<DcLongTaskView> Tasks, bool More);

[Action("dc.space.read", Read = true)]
public sealed record DcReadSpace : IAction<DcSpaceView>
{
    public required string Key { get; init; }
}

[Action("dc.space.list", Read = true)]
public sealed record DcListSpaces : IAction<DcSpacePage>
{
    /// <summary>Keys asked for, any case; null for no key filter.</summary>
    public IReadOnlyList<string>? Keys { get; init; }
    public IReadOnlyList<long>? Ids { get; init; }
    /// <summary>"global" or "personal"; null for both.</summary>
    public string? Type { get; init; }
    /// <summary>"current" or "archived"; null for both.</summary>
    public string? Status { get; init; }
    /// <summary>Space labels asked for. Aerie spaces carry none, so any gives an empty list.</summary>
    public IReadOnlyList<string>? Labels { get; init; }
    public IReadOnlyList<string>? ContentLabels { get; init; }
    /// <summary>True when only favourites, or only spaces with a retention policy, are asked for: aerie has neither, so the list is empty.</summary>
    public bool NoneCanMatch { get; init; }
    public required int Start { get; init; }
    public required int Limit { get; init; }
}

[Action("dc.space.create")]
public sealed record DcCreateSpace : IAction<DcSpaceView>
{
    public string? Key { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    /// <summary>Made with POST /rest/api/space/_private.</summary>
    public bool Private { get; init; }
}

[Action("dc.space.personal.create")]
public sealed record DcCreatePersonalSpace : IAction<DcSpaceView>
{
    public string? Description { get; init; }
    public bool Private { get; init; }
}

[Action("dc.space.update")]
public sealed record DcUpdateSpace : IAction<DcSpaceView>
{
    public required string Key { get; init; }
    public string? BodyKey { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
}

[Action("dc.space.archive")]
public sealed record DcArchiveSpace : IAction<Unit>
{
    public required string Key { get; init; }
    /// <summary>True archives, false restores.</summary>
    public required bool Archive { get; init; }
}

[Action("dc.space.delete")]
public sealed record DcDeleteSpace : IAction<DcLongTaskView>
{
    public required string Key { get; init; }
}

[Action("dc.longtask.list", Read = true)]
public sealed record DcListLongTasks : IAction<DcLongTaskPage>
{
    public required int Start { get; init; }
    public required int Limit { get; init; }
}

[Action("dc.longtask.read", Read = true)]
public sealed record DcReadLongTask : IAction<DcLongTaskView>
{
    public required string Id { get; init; }
}

internal static class DcSpaceData
{
    public const string PersonalPrefix = "~";

    /// <summary>The key aerie stores: lower case (spaces.md, Space keys).</summary>
    public static string Stored(string key) => key.Trim().ToLowerInvariant();

    /// <summary>
    /// Gives every space without one its compatible id, in creation order. Spaces made through aerie's own API get theirs the first time the
    /// surface meets them; the ones that existed before got theirs in the migration.
    /// </summary>
    public static async Task EnsureIdsAsync(KbDbContext db, CancellationToken ct)
    {
        Task<List<Guid>> MissingAsync() => db.Spaces.IgnoreQueryFilters().Where(s => !db.DcSpaces.Any(d => d.SpaceId == s.Id))
            .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id).Select(s => s.Id).ToListAsync(ct);
        if ((await MissingAsync()).Count == 0) return;
        // One request at a time gives them ids, and each looks again once it is its turn: the ids another request gave stand.
        await DcFirstUse.AddAsync(db, async () =>
        {
            var rows = (await MissingAsync()).Select(id => new DcSpace { SpaceId = id }).ToList();
            db.DcSpaces.AddRange(rows);
            return rows;
        }, ct);
    }

    /// <summary>The space under this key when the actor has a role in it, else null (404: a space's existence is not disclosed).</summary>
    public static async Task<(Space Space, DcSpace Dc)?> FindAsync(KbDbContext db, IAccess access, Actor actor, string key, CancellationToken ct)
    {
        await EnsureIdsAsync(db, ct);
        var stored = Stored(key);
        var space = await db.Spaces.FirstOrDefaultAsync(s => s.Key == stored, ct);
        if (space is null || await access.RoleAsync(actor, space.Id, ct) is null) return null;
        return (space, await db.DcSpaces.FirstAsync(d => d.SpaceId == space.Id, ct));
    }

    public static async Task<DcSpaceView> ViewAsync(KbDbContext db, Space s, DcSpace dc, CancellationToken ct)
    {
        var creator = dc.CreatedBy is { } by ? await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == by, ct) : null;
        return new(s.Id, dc.Id, s.Key, s.Name, s.Description, dc.PersonalOwnerId is not null, dc.ArchivedAt is not null,
            creator is null ? null : DcUserData.View(creator), s.CreatedAt);
    }

    /// <summary>404 for no role; 403 for a role that does not manage the space (spaces.md: space administration permission).</summary>
    public static async Task<((Space Space, DcSpace Dc)? Found, Problem? Problem)> ManagedAsync(KbDbContext db, IAccess access, Actor actor, string key, CancellationToken ct)
    {
        if (actor.UserId is null) return (null, Problem.Unauthorized);
        if (await FindAsync(db, access, actor, key, ct) is not { } found) return (null, new Problem(404, "No space with this key is visible to you."));
        return await access.CanAsync(actor, found.Space.Id, SpaceAction.ManageRolesSettingsSensitive, ct)
            ? (found, null) : (null, new Problem(403, "You may not administer this space."));
    }

    /// <summary>A problem from one of aerie's own actions, as the surface states it: a key already in use is 400 here, not 409.</summary>
    public static Problem AsSurface(Problem p) => p.Status == 409 ? new Problem(400, p.Message ?? "This key is already in use.") : p;

    public static DcLongTaskView View(DcLongTask t) => new(t.Id, t.Name, t.Subject, t.StartedAt, t.FinishedAt, t.Successful);
}

internal sealed class DcReadSpaceHandler(KbDbContext db, IAccess access) : IActionHandler<DcReadSpace, DcSpaceView>
{
    private (Space Space, DcSpace Dc)? _found;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcReadSpace> ctx) =>
        (_found = await DcSpaceData.FindAsync(db, access, ctx.Actor, ctx.Action.Key, ctx.Cancel)) is null ? new Problem(404, "No space with this key is visible to you.") : null;

    public async Task<Outcome<DcSpaceView>> HandleAsync(ActionContext<DcReadSpace> ctx) => await DcSpaceData.ViewAsync(db, _found!.Value.Space, _found.Value.Dc, ctx.Cancel);
}

internal sealed class DcListSpacesHandler(KbDbContext db, IAccess access) : IActionHandler<DcListSpaces, DcSpacePage>
{
    // Nothing to refuse: the answer holds only the spaces the caller has a role in.
    public Task<Problem?> AuthorizeAsync(ActionContext<DcListSpaces> ctx) => Task.FromResult<Problem?>(null);

    public async Task<Outcome<DcSpacePage>> HandleAsync(ActionContext<DcListSpaces> ctx)
    {
        var req = ctx.Action;
        if (req.NoneCanMatch || req.Labels is { Count: > 0 }) return new DcSpacePage([], false);
        await DcSpaceData.EnsureIdsAsync(db, ctx.Cancel);
        var visible = await access.VisibleSpacesAsync(ctx.Actor, ctx.Cancel);
        var rows = db.Spaces.AsNoTracking().Where(s => visible.Contains(s.Id))
            .Join(db.DcSpaces.AsNoTracking(), s => s.Id, d => d.SpaceId, (s, d) => new { Space = s, Dc = d });
        if (req.Keys is { } keys)
        {
            var stored = keys.Select(DcSpaceData.Stored).ToList();
            rows = rows.Where(r => stored.Contains(r.Space.Key));
        }
        if (req.Ids is { } ids) rows = rows.Where(r => ids.Contains(r.Dc.Id));
        if (req.Type == "personal") rows = rows.Where(r => r.Dc.PersonalOwnerId != null);
        if (req.Type == "global") rows = rows.Where(r => r.Dc.PersonalOwnerId == null);
        if (req.Status == "archived") rows = rows.Where(r => r.Dc.ArchivedAt != null);
        if (req.Status == "current") rows = rows.Where(r => r.Dc.ArchivedAt == null);
        if (req.ContentLabels is { Count: > 0 } labels)
        {
            var wanted = labels.Select(l => l.Trim().ToLowerInvariant()).ToList();
            var scope = new PageScope(visible);
            var holding = db.Pages.In(scope).Where(p => db.PageTags.Any(t => t.PageId == p.Id && wanted.Contains(t.Tag))).Select(p => p.SpaceId);
            rows = rows.Where(r => holding.Contains(r.Space.Id));
        }
        var page = await rows.OrderBy(r => r.Space.Name).ThenBy(r => r.Space.Key).Skip(req.Start).Take(req.Limit + 1).ToListAsync(ctx.Cancel);
        var views = new List<DcSpaceView>();
        foreach (var r in page.Take(req.Limit)) views.Add(await DcSpaceData.ViewAsync(db, r.Space, r.Dc, ctx.Cancel));
        return new DcSpacePage(views, page.Count > req.Limit);
    }
}

internal sealed class DcCreateSpaceHandler(KbDbContext db, IAccess access, IActionBus bus) : IActionHandler<DcCreateSpace, DcSpaceView>
{
    // Spaces are an Admin's to create (aerie's rule), which is the site permission spaces.md names.
    public async Task<Problem?> AuthorizeAsync(ActionContext<DcCreateSpace> ctx) =>
        ctx.Actor.UserId is null ? Problem.Unauthorized : await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : new Problem(403, "Only an administrator creates spaces.");

    public async Task<Outcome<DcSpaceView>> HandleAsync(ActionContext<DcCreateSpace> ctx)
    {
        var req = ctx.Action;
        if (string.IsNullOrWhiteSpace(req.Key)) return new Problem(400, "key is required.");
        if (string.IsNullOrWhiteSpace(req.Name)) return new Problem(400, "name is required.");
        if (req.Name.Trim().Length > 200) return new Problem(400, "name is at most 200 characters.");
        var key = DcSpaceData.Stored(req.Key);
        if (!SpaceSupport.SpaceKeyPattern().IsMatch(key)) return new Problem(400, "A key is 2 to 32 letters, digits or hyphens, starting with a letter or digit.");
        // spaces.md (Who sees the new space): a global space is read by everyone signed in; one made with _private only by those given a role.
        var visibility = req.Private ? SpaceVisibility.Private : SpaceVisibility.SignedIn;
        var created = await bus.SendAsync(new CreateSpace { Name = req.Name.Trim(), Key = key, Description = req.Description, Visibility = visibility }, CancellationToken.None);
        if (created.Problem is { } p) return DcSpaceData.AsSurface(p);
        await DcSpaceData.EnsureIdsAsync(db, CancellationToken.None);
        var dc = await db.DcSpaces.FirstAsync(d => d.SpaceId == created.Value!.Id, CancellationToken.None);
        dc.CreatedBy = ctx.Actor.UserId;
        await db.SaveChangesAsync(CancellationToken.None);
        return await DcSpaceData.ViewAsync(db, await db.Spaces.FirstAsync(s => s.Id == dc.SpaceId, CancellationToken.None), dc, CancellationToken.None);
    }
}

internal sealed class DcCreatePersonalSpaceHandler(KbDbContext db, IAccess access, TimeProvider time) : IActionHandler<DcCreatePersonalSpace, DcSpaceView>
{
    private KbUser? _me;

    // Only an Admin creates spaces in aerie; in T1 a personal space is one too (spaces.md).
    public async Task<Problem?> AuthorizeAsync(ActionContext<DcCreatePersonalSpace> ctx)
    {
        if (ctx.Actor.UserId is not { } me) return Problem.Unauthorized;
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return new Problem(403, "Only an administrator creates spaces.");
        _me = await db.Users.FirstAsync(u => u.Id == me, ctx.Cancel);
        return null;
    }

    public async Task<Outcome<DcSpaceView>> HandleAsync(ActionContext<DcCreatePersonalSpace> ctx)
    {
        var me = _me!;
        if (await db.DcSpaces.AnyAsync(d => d.PersonalOwnerId == me.Id, ctx.Cancel)) return new Problem(409, "You already have a personal space.");
        var key = DcSpaceData.PersonalPrefix + (me.UserName ?? "").ToLowerInvariant();
        if (await db.Spaces.IgnoreQueryFilters().AnyAsync(s => s.Key == key, ctx.Cancel)) return new Problem(409, "You already have a personal space.");
        var description = ctx.Action.Description;
        var space = new Space { Key = key, Name = me.DisplayName.Length > 0 ? me.DisplayName : me.UserName!, Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            Visibility = SpaceVisibility.Private, CreatedAt = time.GetUtcNow() };
        db.Spaces.Add(space);
        db.SpaceRoleAssignments.Add(new SpaceRoleAssignment { SpaceId = space.Id, PrincipalType = PrincipalType.User, PrincipalId = me.Id.ToString(), RoleId = SpacePerms.IdOf(SpaceRole.Owner) });
        var dc = new DcSpace { SpaceId = space.Id, CreatedBy = me.Id, PersonalOwnerId = me.Id };
        db.DcSpaces.Add(dc);
        await db.SaveChangesAsync();
        ctx.Audit(AuditActions.SpaceCreate, "space", space.Id.ToString(), space.Id, new Dictionary<string, string> { ["key"] = key, ["personal"] = "true" });
        return await DcSpaceData.ViewAsync(db, space, dc, CancellationToken.None);
    }
}

internal sealed class DcUpdateSpaceHandler(KbDbContext db, IAccess access, IActionBus bus) : IActionHandler<DcUpdateSpace, DcSpaceView>
{
    private (Space Space, DcSpace Dc)? _found;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcUpdateSpace> ctx)
    {
        (_found, var problem) = await DcSpaceData.ManagedAsync(db, access, ctx.Actor, ctx.Action.Key, ctx.Cancel);
        return problem;
    }

    public async Task<Outcome<DcSpaceView>> HandleAsync(ActionContext<DcUpdateSpace> ctx)
    {
        var (req, (space, dc)) = (ctx.Action, _found!.Value);
        if (string.IsNullOrWhiteSpace(req.Name)) return new Problem(400, "name is required.");
        if (req.Name.Trim().Length > 200) return new Problem(400, "name is at most 200 characters.");
        if (string.IsNullOrWhiteSpace(req.BodyKey) || DcSpaceData.Stored(req.BodyKey) != space.Key) return new Problem(400, "key must be the space's key; it cannot change.");
        // A rename is an Admin's (aerie's rule): aerie's own update refuses it to an Owner with 403.
        var updated = await bus.SendAsync(new UpdateSpace { SpaceId = space.Id, Name = req.Name.Trim(), Description = req.Description ?? space.Description ?? "" }, CancellationToken.None);
        if (updated.Problem is { } p) return DcSpaceData.AsSurface(p);
        var fresh = await db.Spaces.AsNoTracking().FirstAsync(s => s.Id == space.Id, CancellationToken.None);
        return await DcSpaceData.ViewAsync(db, fresh, dc, CancellationToken.None);
    }
}

internal sealed class DcArchiveSpaceHandler(KbDbContext db, IAccess access) : IActionHandler<DcArchiveSpace, Unit>
{
    private (Space Space, DcSpace Dc)? _found;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcArchiveSpace> ctx)
    {
        (_found, var problem) = await DcSpaceData.ManagedAsync(db, access, ctx.Actor, ctx.Action.Key, ctx.Cancel);
        return problem;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<DcArchiveSpace> ctx)
    {
        var (space, dc) = _found!.Value;
        // Idempotent: archiving an archived space changes nothing and writes no entry.
        if (dc.ArchivedAt is not null == ctx.Action.Archive) return new Unit();
        dc.ArchivedAt = ctx.Action.Archive ? ctx.Services.GetRequiredService<TimeProvider>().GetUtcNow() : null;
        await db.SaveChangesAsync();
        ctx.Audit(ctx.Action.Archive ? AuditActions.SpaceArchive : AuditActions.SpaceUnarchive, "space", space.Id.ToString(), space.Id, new Dictionary<string, string> { ["key"] = space.Key });
        return new Unit();
    }
}

internal sealed class DcDeleteSpaceHandler(KbDbContext db, IAccess access, IActionBus bus, TimeProvider time) : IActionHandler<DcDeleteSpace, DcLongTaskView>
{
    private (Space Space, DcSpace Dc)? _found;

    // Deleting a space is an Admin's in aerie, whatever role anyone has in it.
    public async Task<Problem?> AuthorizeAsync(ActionContext<DcDeleteSpace> ctx)
    {
        if (ctx.Actor.UserId is null) return Problem.Unauthorized;
        if ((_found = await DcSpaceData.FindAsync(db, access, ctx.Actor, ctx.Action.Key, ctx.Cancel)) is null) return new Problem(404, "No space with this key is visible to you.");
        return await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : new Problem(403, "Only an administrator deletes spaces.");
    }

    public async Task<Outcome<DcLongTaskView>> HandleAsync(ActionContext<DcDeleteSpace> ctx)
    {
        var space = _found!.Value.Space;
        var task = new DcLongTask { UserId = ctx.Actor.UserId, Name = "space.delete", Subject = space.Key.ToUpperInvariant(), StartedAt = time.GetUtcNow() };
        // Aerie's own delete, with the phrase it asks for: the space leaves every read at once, into the Admins' recycle bin.
        var deleted = await bus.SendAsync(new DeleteSpace { SpaceId = space.Id, Confirm = $"delete {space.Key}" }, CancellationToken.None);
        if (deleted.Problem is { } p) return p;
        (task.FinishedAt, task.Successful) = (time.GetUtcNow(), true);
        db.DcLongTasks.Add(task);
        await db.SaveChangesAsync();
        return DcSpaceData.View(task);
    }
}

internal sealed class DcListLongTasksHandler(KbDbContext db, IAccess access) : IActionHandler<DcListLongTasks, DcLongTaskPage>
{
    private bool _admin;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcListLongTasks> ctx)
    {
        if (ctx.Actor.UserId is null) return Problem.Unauthorized;
        _admin = await access.IsAdminAsync(ctx.Actor, ctx.Cancel);
        return null;
    }

    public async Task<Outcome<DcLongTaskPage>> HandleAsync(ActionContext<DcListLongTasks> ctx)
    {
        var (me, req) = (ctx.Actor.UserId, ctx.Action);
        // shortcut: task records are kept for good (common.md asks for at least 24 hours); prune old ones if the table grows.
        var rows = await db.DcLongTasks.AsNoTracking().Where(t => _admin || t.UserId == me).OrderByDescending(t => t.StartedAt).ThenBy(t => t.Id)
            .Skip(req.Start).Take(req.Limit + 1).ToListAsync(ctx.Cancel);
        return new DcLongTaskPage([.. rows.Take(req.Limit).Select(DcSpaceData.View)], rows.Count > req.Limit);
    }
}

internal sealed class DcReadLongTaskHandler(KbDbContext db, IAccess access) : IActionHandler<DcReadLongTask, DcLongTaskView>
{
    private DcLongTask? _task;

    // Someone who did not start the task, and is not an Admin, gets 404.
    public async Task<Problem?> AuthorizeAsync(ActionContext<DcReadLongTask> ctx)
    {
        if (ctx.Actor.UserId is not { } me) return Problem.Unauthorized;
        var none = new Problem(404, "No such task.");
        if (!Guid.TryParseExact(ctx.Action.Id, "N", out var id) && !Guid.TryParse(ctx.Action.Id, out id)) return none;
        _task = await db.DcLongTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ctx.Cancel);
        if (_task is null) return none;
        return _task.UserId == me || await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : none;
    }

    public Task<Outcome<DcLongTaskView>> HandleAsync(ActionContext<DcReadLongTask> ctx) => Task.FromResult<Outcome<DcLongTaskView>>(DcSpaceData.View(_task!));
}
