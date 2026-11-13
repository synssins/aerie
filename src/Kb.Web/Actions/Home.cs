using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Core.Identity;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

internal sealed class ReadHomeHandler(KbDbContext db, IAccess access, TimeProvider time) : IActionHandler<ReadHome, HomeDto>
{
    // Announcements are for everyone who is signed in; the pages are only those of the caller's spaces.
    public Task<Problem?> AuthorizeAsync(ActionContext<ReadHome> ctx) => Task.FromResult(ctx.Actor.Kind == ActorKind.Anonymous ? Problem.Unauthorized : null);

    public async Task<Outcome<HomeDto>> HandleAsync(ActionContext<ReadHome> ctx)
    {
        var now = time.GetUtcNow();
        var announcements = await (from a in db.Announcements.AsNoTracking()
                                   where a.RemovedAt == null && (a.ExpiresAt == null || a.ExpiresAt > now)
                                   orderby a.CreatedAt descending
                                   join u in db.Users on a.CreatedBy equals u.Id into au
                                   from u in au.DefaultIfEmpty()
                                   select new AnnouncementDto(a.Id, a.Title, a.Body, u == null ? "Unknown" : u.DisplayName, a.CreatedAt, a.ExpiresAt)).Take(5).ToListAsync(ctx.Cancel);

        var scope = await PageScopes.ForAsync(access, ctx.Actor, null, ctx.Cancel);
        var recent = await (from p in db.Pages.AsNoTracking().In(scope)
                            where p.Kind == PageKind.Page
                            orderby p.UpdatedAt descending
                            join s in db.Spaces on p.SpaceId equals s.Id
                            join u in db.Users on p.UpdatedBy equals u.Id into pu
                            from u in pu.DefaultIfEmpty()
                            select new RecentPageDto(p.Id, p.Title, p.SpaceId, s.Name, p.UpdatedAt, u == null ? "Unknown" : u.DisplayName)).Take(10).ToListAsync(ctx.Cancel);
        return new HomeDto(announcements, recent);
    }
}

internal sealed class PostAnnouncementHandler(KbDbContext db, TimeProvider time) : IActionHandler<PostAnnouncement, AnnouncementPostedDto>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<PostAnnouncement> ctx) => Announcers.CheckAsync(db, ctx.Actor, ctx.Cancel);

    public async Task<Outcome<AnnouncementPostedDto>> HandleAsync(ActionContext<PostAnnouncement> ctx)
    {
        var (req, me) = (ctx.Action, ctx.Actor.UserId!.Value);
        var title = req.Title?.Trim() ?? "";
        var body = req.Body?.Trim() ?? "";
        var now = time.GetUtcNow();
        if (title.Length is 0 or > 120 || body.Length > 2000 || req.ExpiresAt <= now)
            return Problem.Invalid("announcement", "Title 1-120 characters, text up to 2,000, end date in the future.");
        var a = new Announcement { Title = title, Body = body, CreatedBy = me, CreatedAt = now, ExpiresAt = req.ExpiresAt };
        db.Announcements.Add(a);
        await db.SaveChangesAsync();
        ctx.Audit(AuditActions.AnnouncementPost, "announcement", a.Id.ToString(), data: new Dictionary<string, string> { ["title"] = title });
        return new AnnouncementPostedDto(a.Id);
    }
}

internal sealed class RemoveAnnouncementHandler(KbDbContext db, TimeProvider time) : IActionHandler<RemoveAnnouncement, Unit>
{
    private Announcement? _announcement;

    public async Task<Problem?> AuthorizeAsync(ActionContext<RemoveAnnouncement> ctx)
    {
        if (await Announcers.CheckAsync(db, ctx.Actor, ctx.Cancel) is { } denied) return denied;
        var id = ctx.Action.AnnouncementId;
        return (_announcement = await db.Announcements.FirstOrDefaultAsync(x => x.Id == id && x.RemovedAt == null, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<RemoveAnnouncement> ctx)
    {
        var a = _announcement!;
        a.RemovedAt = time.GetUtcNow();
        await db.SaveChangesAsync();
        ctx.Audit(AuditActions.AnnouncementRemove, "announcement", a.Id.ToString(), data: new Dictionary<string, string> { ["title"] = a.Title });
        return new Unit();
    }
}

/// <summary>Who may post announcements. Separate from Admin and from space roles. An Admin's: AuthorizeAsync answers 403 to anyone else.</summary>
internal sealed class SetAnnouncerHandler(KbDbContext db, IAccess access) : IActionHandler<SetAnnouncer, Unit>
{
    private KbUser? _user;

    public async Task<Problem?> AuthorizeAsync(ActionContext<SetAnnouncer> ctx)
    {
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel)) return Problem.Forbidden;
        var id = ctx.Action.UserId;
        return (_user = await db.Users.FirstOrDefaultAsync(x => x.Id == id, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<SetAnnouncer> ctx)
    {
        var (user, enabled) = (_user!, ctx.Action.Enabled);
        user.CanAnnounce = enabled;
        await db.SaveChangesAsync();
        ctx.Audit(AuditActions.AnnouncerSet, "user", user.Id.ToString(), data: new Dictionary<string, string> { ["enabled"] = enabled.ToString() });
        return new Unit();
    }
}

file static class Announcers
{
    /// <summary>
    /// Posting and removing announcements is for an Admin and for a user an Admin allowed. It is read from the caller's own row, not from a space role.
    /// 401 for an actor that is not a person: an announcement names who posted it.
    /// </summary>
    public static async Task<Problem?> CheckAsync(KbDbContext db, Actor actor, CancellationToken ct)
    {
        if (actor.UserId is not { } me) return Problem.Unauthorized;
        return await db.Users.AsNoTracking().AnyAsync(u => u.Id == me && u.DeactivatedAt == null && (u.IsAdmin || u.CanAnnounce), ct) ? null : Problem.Forbidden;
    }
}
