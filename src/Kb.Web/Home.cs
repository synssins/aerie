using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Core.Spaces;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web;

public sealed record AnnouncementDto(Guid Id, string Title, string Body, string By, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);
public sealed record PostAnnouncementRequest(string Title, string? Body, DateTimeOffset? ExpiresAt);
public sealed record RecentPageDto(Guid Id, string Title, Guid SpaceId, string SpaceName, DateTimeOffset UpdatedAt, string UpdatedBy);
public sealed record HomeDto(IReadOnlyList<AnnouncementDto> Announcements, IReadOnlyList<RecentPageDto> Recent);
public sealed record AnnouncerRequest(bool Enabled);

public static class HomeEndpoints
{
    public static void MapHomeEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/home", async (HttpContext http, ISpacePermissionService perms, KbDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var now = time.GetUtcNow();
            var announcements = await (from a in db.Announcements.AsNoTracking()
                              where a.RemovedAt == null && (a.ExpiresAt == null || a.ExpiresAt > now)
                              orderby a.CreatedAt descending
                              join u in db.Users on a.CreatedBy equals u.Id into au
                              from u in au.DefaultIfEmpty()
                              select new AnnouncementDto(a.Id, a.Title, a.Body, u == null ? "Unknown" : u.DisplayName, a.CreatedAt, a.ExpiresAt)).Take(5).ToListAsync(ct);

            var visible = await perms.VisibleSpaceIdsAsync(http.User.UserId(), ct);
            var recent = await (from p in db.Pages.AsNoTracking()
                                where p.Kind == PageKind.Page && visible.Contains(p.SpaceId)
                                orderby p.UpdatedAt descending
                                join s in db.Spaces on p.SpaceId equals s.Id
                                join u in db.Users on p.UpdatedBy equals u.Id into pu
                                from u in pu.DefaultIfEmpty()
                                select new RecentPageDto(p.Id, p.Title, p.SpaceId, s.Name, p.UpdatedAt, u == null ? "Unknown" : u.DisplayName)).Take(10).ToListAsync(ct);
            return Results.Ok(new HomeDto(announcements, recent));
        });

        api.MapPost("/announcements", async (PostAnnouncementRequest req, HttpContext http, KbDbContext db, IAuditLog audit, TimeProvider time, CancellationToken ct) =>
        {
            var me = http.User.UserId();
            if (!await CanAnnounceAsync(db, me, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var title = req.Title?.Trim() ?? "";
            var body = req.Body?.Trim() ?? "";
            var now = time.GetUtcNow();
            if (title.Length is 0 or > 120 || body.Length > 2000 || req.ExpiresAt <= now)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["announcement"] = ["Title 1-120 characters, text up to 2,000, end date in the future."] });
            var a = new Announcement { Title = title, Body = body, CreatedBy = me, CreatedAt = now, ExpiresAt = req.ExpiresAt };
            db.Announcements.Add(a);
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(new AuditEvent(AuditActions.AnnouncementPost, me, "announcement", a.Id.ToString(), Data: new Dictionary<string, string> { ["title"] = title }), ct);
            return Results.Created($"/api/v1/announcements/{a.Id}", new { id = a.Id });
        });

        api.MapDelete("/announcements/{id:guid}", async (Guid id, HttpContext http, KbDbContext db, IAuditLog audit, TimeProvider time, CancellationToken ct) =>
        {
            var me = http.User.UserId();
            if (!await CanAnnounceAsync(db, me, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var a = await db.Announcements.FirstOrDefaultAsync(x => x.Id == id && x.RemovedAt == null, ct);
            if (a is null) return Results.NotFound();
            a.RemovedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(new AuditEvent(AuditActions.AnnouncementRemove, me, "announcement", id.ToString(), Data: new Dictionary<string, string> { ["title"] = a.Title }), ct);
            return Results.NoContent();
        });

        // Who may post announcements. Separate from Admin and from space roles.
        api.AdminGroup().MapPut("/users/{id:guid}/announcer", async (Guid id, AnnouncerRequest req, HttpContext http, KbDbContext db, IAuditLog audit, CancellationToken ct) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (u is null) return Results.NotFound();
            u.CanAnnounce = req.Enabled;
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(new AuditEvent(AuditActions.AnnouncerSet, http.User.UserId(), "user", id.ToString(), Data: new Dictionary<string, string> { ["enabled"] = req.Enabled.ToString() }), ct);
            return Results.NoContent();
        });
    }

    private static Task<bool> CanAnnounceAsync(KbDbContext db, Guid me, CancellationToken ct) =>
        db.Users.AsNoTracking().AnyAsync(u => u.Id == me && u.DeactivatedAt == null && (u.IsAdmin || u.CanAnnounce), ct);
}
