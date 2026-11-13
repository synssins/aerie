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

public sealed record AdminUserDto(Guid Id, string UserName, string DisplayName, string Source, bool IsAdmin, bool IsBreakGlass, DateTimeOffset CreatedAt, DateTimeOffset? DeactivatedAt, bool CanAnnounce);
public sealed record AuditEntryDto(long Sequence, DateTimeOffset At, string Action, string? Actor, string? TargetType, string? TargetId);
public sealed record InviteRequest(string UserName, string DisplayName, string? Email, bool IsAdmin = false);

public static partial class EndpointMappings
{
    public static void MapAdminEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").AddEndpointFilter(async (ctx, next) =>
            await IsAdminAsync(ctx.HttpContext, ctx.HttpContext.RequestServices.GetRequiredService<KbDbContext>()) ? await next(ctx) : Results.StatusCode(403));

        admin.MapGet("/audit/verify", async (IAuditLog audit) => Results.Ok(await audit.VerifyAsync()));

        admin.MapGet("/audit", async (KbDbContext db, int? take) =>
        {
            var n = Math.Clamp(take ?? 100, 1, 500);
            var rows = await (from a in db.AuditEntries.AsNoTracking()
                              orderby a.Sequence descending
                              join u in db.Users on a.ActorId equals u.Id into au
                              from u in au.DefaultIfEmpty()
                              select new AuditEntryDto(a.Sequence, a.At, a.Action, u == null ? null : u.DisplayName, a.TargetType, a.TargetId))
                             .Take(n).ToListAsync();
            return Results.Ok(rows);
        });

        admin.MapGet("/spaces", async (KbDbContext db) =>
            Results.Ok((await db.Spaces.AsNoTracking().OrderBy(x => x.Name).ToListAsync()).Select(x => ToDto(x, null))));

        admin.MapPatch("/spaces/{id:guid}", async (Guid id, UpdateSpaceRequest req, HttpContext http, KbDbContext db, IAuditLog audit) =>
        {
            var s = await db.Spaces.FirstOrDefaultAsync(x => x.Id == id);
            if (s is null) return Results.NotFound();
            return await UpdateSpaceAsync(s, req, isAdmin: true, http.User.UserId(), db, audit) ?? Results.Ok(ToDto(s, null));
        });

        admin.MapGet("/users", async (KbDbContext db) => Results.Ok(await db.Users.AsNoTracking().OrderBy(u => u.DisplayName)
            .Select(u => new AdminUserDto(u.Id, u.UserName!, u.DisplayName, u.Source.ToString(), u.IsAdmin, u.IsBreakGlass, u.CreatedAt, u.DeactivatedAt, u.CanAnnounce))
            .ToListAsync()));

        admin.MapPost("/users/invite", async (InviteRequest req, HttpContext http, UserManager<KbUser> users, IAuditLog audit, TimeProvider time) =>
        {
            if (string.IsNullOrWhiteSpace(req.UserName)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["userName"] = ["Required."] });
            var u = new KbUser { UserName = req.UserName.Trim(), DisplayName = req.DisplayName, Email = req.Email, Source = UserSource.Local, IsAdmin = req.IsAdmin, CreatedAt = time.GetUtcNow(), LockoutEnabled = true };
            var result = await users.CreateAsync(u);
            if (!result.Succeeded) return Results.ValidationProblem(new Dictionary<string, string[]> { ["user"] = result.Errors.Select(e => e.Description).ToArray() });
            var token = await users.GeneratePasswordResetTokenAsync(u);
            await audit.AppendAsync(new AuditEvent(AuditActions.UserInvite, http.User.UserId(), "user", u.Id.ToString(), Data: new Dictionary<string, string> { ["isAdmin"] = req.IsAdmin.ToString() }));
            // shortcut: the set-password token is returned to the inviting admin; replace with an emailed one-time link once email is configured.
            return Results.Ok(new { u.Id, setPasswordToken = token });
        });

        admin.MapPost("/auth/local/disable", async (HttpContext http, KbDbContext db, IAuditLog audit, TimeProvider time) =>
        {
            if (await db.GetAsync(RuntimeSettingKeys.ExternalAdminVerifiedAt) is null)
                return Results.Conflict(new { reason = "external-admin-not-verified" });
            await db.SetAsync(RuntimeSettingKeys.LocalSignInDisabled, "true", http.User.UserId(), time.GetUtcNow());
            await audit.AppendAsync(new AuditEvent(AuditActions.SettingsChange, http.User.UserId(), "setting", RuntimeSettingKeys.LocalSignInDisabled, Data: new Dictionary<string, string> { ["value"] = "true" }));
            return Results.NoContent();
        });

        admin.MapPut("/settings/public-space", async (bool enabled, HttpContext http, KbDbContext db, IAuditLog audit, TimeProvider time) =>
        {
            await db.SetAsync(RuntimeSettingKeys.PublicSpaceEnabled, enabled ? "true" : "false", http.User.UserId(), time.GetUtcNow());
            await audit.AppendAsync(new AuditEvent(AuditActions.SettingsChange, http.User.UserId(), "setting", RuntimeSettingKeys.PublicSpaceEnabled, Data: new Dictionary<string, string> { ["value"] = enabled.ToString() }));
            return Results.NoContent();
        });
    }
}
