using Kb.Core.Access;
using Kb.Core.Audit;
using Kb.Core.Config;
using Kb.Core.Identity;
using Kb.Core.Plugins;
using Kb.Core.Spaces;
using Kb.Data;
using Kb.Plugins;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace Kb.Web;

public sealed record AccessRequestBody(SpaceRole Role, string? Reason);
public sealed record DecideBody(bool Approve);
public sealed record EmergencyBody(string Reason);

public static partial class EndpointMappings
{
    public static void MapAccessEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/spaces/{id:guid}/access-requests", async (Guid id, AccessRequestBody body, HttpContext http, AccessService svc) =>
        {
            try { var r = await svc.RequestAsync(id, http.User.UserId(), body.Role, body.Reason); return Results.Accepted(value: new { r.Id }); }
            catch (NotFoundException) { return Results.NotFound(); }
        });

        api.MapPost("/access-requests/{id:guid}/decision", async (Guid id, DecideBody body, HttpContext http, AccessService svc) =>
        {
            try { await svc.DecideAsync(id, http.User.UserId(), body.Approve); return Results.NoContent(); }
            catch (NotFoundException) { return Results.NotFound(); }
            catch (AccessDeniedException) { return Results.StatusCode(403); }
            catch (InvalidOperationException e) { return Results.Conflict(new { error = e.Message }); }
        });

        api.MapPost("/spaces/{id:guid}/emergency-access", async (Guid id, EmergencyBody body, HttpContext http, AccessService svc) =>
        {
            try { var g = await svc.OpenEmergencyAsync(id, http.User.UserId(), body.Reason); return Results.Ok(new { g.ExpiresAt }); }
            catch (NotFoundException) { return Results.NotFound(); }
            catch (AccessDeniedException) { return Results.StatusCode(403); }
            catch (ArgumentException e) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["reason"] = [e.Message] }); }
            catch (InvalidOperationException e) { return Results.Conflict(new { error = e.Message }); }
        });
    }

    public static void MapNotificationEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/notifications", async (bool? unread, HttpContext http, KbDbContext db) =>
        {
            var me = http.User.UserId();
            var q = db.Notifications.AsNoTracking().Where(n => n.UserId == me);
            if (unread == true) q = q.Where(n => n.ReadAt == null);
            var list = await q.ToListAsync();
            return Results.Ok(list.OrderByDescending(n => n.CreatedAt).Take(100));
        });

        api.MapGet("/notifications/count", async (HttpContext http, KbDbContext db) =>
        {
            var me = http.User.UserId();
            return Results.Ok(new { unread = await db.Notifications.CountAsync(n => n.UserId == me && n.ReadAt == null) });
        });

        api.MapPost("/notifications/{id:guid}/read", async (Guid id, HttpContext http, KbDbContext db, TimeProvider time) =>
        {
            var me = http.User.UserId();
            var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == me);
            if (n is null) return Results.NotFound();
            n.ReadAt ??= time.GetUtcNow();
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        api.MapPost("/notifications/read-all", async (HttpContext http, KbDbContext db, TimeProvider time) =>
        {
            var me = http.User.UserId();
            var now = time.GetUtcNow();
            foreach (var n in await db.Notifications.Where(x => x.UserId == me && x.ReadAt == null).ToListAsync()) n.ReadAt = now;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
