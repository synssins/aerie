using System.Text.RegularExpressions;
using Kb.Core.Access;
using Kb.Core.Audit;
using Kb.Core.Config;
using Kb.Core.Identity;
using Kb.Core.Plugins;
using Kb.Core.Spaces;
using Kb.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kb.Web;

public sealed record SignInRequest(string UserName, string Password);
public sealed record MeResponse(Guid Id, string UserName, string DisplayName, bool IsAdmin, bool IsBreakGlass);
public sealed record SpaceDto(Guid Id, string Key, string Name, string? Description, bool IsSensitive, bool IsPublic, string? MyRole);
public sealed record CreateSpaceRequest(string Key, string Name, string? Description, bool IsSensitive = false, bool IsPublic = false);
public sealed record UpdateSpaceRequest(string? Name, string? Description, bool? IsSensitive, bool? IsPublic);
public sealed record RoleAssignmentDto(Guid Id, PrincipalType PrincipalType, string PrincipalId, SpaceRole Role);
public sealed record GrantRoleRequest(PrincipalType PrincipalType, string PrincipalId, SpaceRole Role);
public sealed record AccessRequestBody(SpaceRole Role, string? Reason);
public sealed record DecideBody(bool Approve);
public sealed record EmergencyBody(string Reason);
public sealed record InviteRequest(string UserName, string DisplayName, string? Email, bool IsAdmin = false);
public sealed record PluginDto(string Id, string Version, string Publisher, string Origin, bool Enabled, IReadOnlyList<string> ExtensionPoints, IReadOnlyList<string> OutboundHosts);

public static partial class EndpointMappings
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,31}$")]
    private static partial Regex SpaceKeyPattern();

    public static void MapAuthEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/auth/local/signin", async (SignInRequest req, SignInManager<KbUser> signIn, UserManager<KbUser> users,
            KbDbContext db, IAuditLog audit, BreakGlassService breakGlass, HttpContext http) =>
        {
            var user = await users.FindByNameAsync(req.UserName);
            // Same response for unknown user and wrong password: no account enumeration.
            if (user is null || user.DeactivatedAt is not null) return Results.Unauthorized();
            if (!user.IsBreakGlass && await db.GetAsync(RuntimeSettingKeys.LocalSignInDisabled) == "true") return Results.StatusCode(403);

            var result = await signIn.PasswordSignInAsync(user, req.Password, isPersistent: false, lockoutOnFailure: true);
            if (!result.Succeeded)
            {
                await audit.AppendAsync(new AuditEvent(AuditActions.SignInFailed, user.Id, "user", user.Id.ToString(),
                    Data: new Dictionary<string, string> { ["lockedOut"] = result.IsLockedOut.ToString() }));
                return Results.Unauthorized();
            }
            await audit.AppendAsync(new AuditEvent(AuditActions.SignIn, user.Id, "user", user.Id.ToString(), Data: new Dictionary<string, string> { ["method"] = "local" }));
            if (user.IsBreakGlass) await breakGlass.OnSignInAsync(user, http.Connection.RemoteIpAddress?.ToString());
            return Results.Ok(new MeResponse(user.Id, user.UserName!, user.DisplayName, user.IsAdmin, user.IsBreakGlass));
        }).AllowAnonymous();

        api.MapPost("/auth/signout", async (SignInManager<KbUser> signIn) => { await signIn.SignOutAsync(); return Results.NoContent(); });

        api.MapGet("/me", async (HttpContext http, UserManager<KbUser> users) =>
        {
            var u = await users.FindByIdAsync(http.User.UserId().ToString());
            return u is null ? Results.Unauthorized() : Results.Ok(new MeResponse(u.Id, u.UserName!, u.DisplayName, u.IsAdmin, u.IsBreakGlass));
        });

        api.MapGet("/brand", (IOptions<KbOptions> o) => Results.Ok(o.Value.Brand)).AllowAnonymous();
    }

    public static void MapSpaceEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/spaces", async (HttpContext http, ISpacePermissionService perms, KbDbContext db) =>
        {
            var me = http.User.UserId();
            var ids = await perms.VisibleSpaceIdsAsync(me);
            var spaces = await db.Spaces.AsNoTracking().Where(s => ids.Contains(s.Id)).OrderBy(s => s.Name).ToListAsync();
            var list = new List<SpaceDto>();
            foreach (var s in spaces) list.Add(ToDto(s, await perms.GetRoleAsync(me, s.Id)));
            return Results.Ok(list);
        });

        // 404 for both "missing" and "no access": a space's existence is not disclosed.
        api.MapGet("/spaces/{id:guid}", async (Guid id, HttpContext http, ISpacePermissionService perms, KbDbContext db) =>
        {
            var role = await perms.GetRoleAsync(http.User.UserId(), id);
            if (role is null) return Results.NotFound();
            var s = await db.Spaces.AsNoTracking().FirstAsync(x => x.Id == id);
            return Results.Ok(ToDto(s, role));
        });

        api.MapPost("/spaces", async (CreateSpaceRequest req, HttpContext http, KbDbContext db, IAuditLog audit, TimeProvider time) =>
        {
            if (!await IsAdminAsync(http, db)) return Results.StatusCode(403);
            if (!SpaceKeyPattern().IsMatch(req.Key)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["key"] = ["2-32 chars: lowercase letters, digits, hyphen."] });
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Required."] });
            if (await db.Spaces.AnyAsync(s => s.Key == req.Key)) return Results.Conflict();
            var s = new Space { Key = req.Key, Name = req.Name.Trim(), Description = req.Description, IsSensitive = req.IsSensitive, IsPublic = req.IsPublic, CreatedAt = time.GetUtcNow() };
            db.Spaces.Add(s);
            await db.SaveChangesAsync();
            await audit.AppendAsync(new AuditEvent(AuditActions.SpaceCreate, http.User.UserId(), "space", s.Id.ToString(), s.Id));
            return Results.Created($"/api/v1/spaces/{s.Id}", ToDto(s, SpaceRole.Owner));
        });

        api.MapPatch("/spaces/{id:guid}", async (Guid id, UpdateSpaceRequest req, HttpContext http, ISpacePermissionService perms, KbDbContext db, IAuditLog audit) =>
        {
            var me = http.User.UserId();
            if (await perms.GetRoleAsync(me, id) is null) return Results.NotFound();
            if (!await perms.CanAsync(me, id, SpaceAction.ManageRolesSettingsSensitive)) return Results.StatusCode(403);
            var s = await db.Spaces.FirstAsync(x => x.Id == id);
            if (req.Name is { } n && !string.IsNullOrWhiteSpace(n)) s.Name = n.Trim();
            if (req.Description is { } d) s.Description = d;
            if (req.IsPublic is { } p) s.IsPublic = p;
            var sensitiveChanged = req.IsSensitive is { } sens && sens != s.IsSensitive;
            if (req.IsSensitive is { } v) s.IsSensitive = v;
            await db.SaveChangesAsync();
            await audit.AppendAsync(new AuditEvent(sensitiveChanged ? AuditActions.SpaceSensitiveSet : AuditActions.SpaceUpdate, me, "space", id.ToString(), id,
                new Dictionary<string, string> { ["isSensitive"] = s.IsSensitive.ToString(), ["isPublic"] = s.IsPublic.ToString() }));
            return Results.Ok(ToDto(s, await perms.GetRoleAsync(me, id)));
        });

        api.MapGet("/spaces/{id:guid}/roles", async (Guid id, HttpContext http, ISpacePermissionService perms, KbDbContext db) =>
        {
            var me = http.User.UserId();
            if (await perms.GetRoleAsync(me, id) is null) return Results.NotFound();
            if (!await perms.CanAsync(me, id, SpaceAction.ManageRolesSettingsSensitive)) return Results.StatusCode(403);
            var list = await db.SpaceRoleAssignments.AsNoTracking().Where(a => a.SpaceId == id)
                .Select(a => new RoleAssignmentDto(a.Id, a.PrincipalType, a.PrincipalId, a.Role)).ToListAsync();
            return Results.Ok(list);
        });

        api.MapPut("/spaces/{id:guid}/roles", async (Guid id, GrantRoleRequest req, HttpContext http, ISpacePermissionService perms, IAccessData access, IAuditLog audit) =>
        {
            var me = http.User.UserId();
            if (await perms.GetRoleAsync(me, id) is null) return Results.NotFound();
            if (!await perms.CanAsync(me, id, SpaceAction.ManageRolesSettingsSensitive)) return Results.StatusCode(403);
            if (string.IsNullOrWhiteSpace(req.PrincipalId) || !Enum.IsDefined(req.Role)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["principal"] = ["Invalid."] });
            await access.AddAssignmentAsync(new SpaceRoleAssignment { SpaceId = id, PrincipalType = req.PrincipalType, PrincipalId = req.PrincipalId, Role = req.Role }, default);
            await audit.AppendAsync(new AuditEvent(AuditActions.SpaceRoleGrant, me, req.PrincipalType.ToString(), req.PrincipalId, id, new Dictionary<string, string> { ["role"] = req.Role.ToString() }));
            return Results.NoContent();
        });

        api.MapDelete("/spaces/{id:guid}/roles/{assignmentId:guid}", async (Guid id, Guid assignmentId, HttpContext http, ISpacePermissionService perms, KbDbContext db, IAuditLog audit) =>
        {
            var me = http.User.UserId();
            if (await perms.GetRoleAsync(me, id) is null) return Results.NotFound();
            if (!await perms.CanAsync(me, id, SpaceAction.ManageRolesSettingsSensitive)) return Results.StatusCode(403);
            var a = await db.SpaceRoleAssignments.FirstOrDefaultAsync(x => x.Id == assignmentId && x.SpaceId == id);
            if (a is null) return Results.NotFound();
            db.SpaceRoleAssignments.Remove(a);
            await db.SaveChangesAsync();
            await audit.AppendAsync(new AuditEvent(AuditActions.SpaceRoleRevoke, me, a.PrincipalType.ToString(), a.PrincipalId, id));
            return Results.NoContent();
        });
    }

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

    public static void MapAdminEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").AddEndpointFilter(async (ctx, next) =>
            await IsAdminAsync(ctx.HttpContext, ctx.HttpContext.RequestServices.GetRequiredService<KbDbContext>()) ? await next(ctx) : Results.StatusCode(403));

        admin.MapGet("/plugins", (PluginRegistry reg) => Results.Ok(new
        {
            loaded = reg.All.Select(p => new PluginDto(p.Manifest.Id, p.Manifest.Version, p.Manifest.Publisher, p.Origin.ToString(), p.Enabled, p.Manifest.ExtensionPoints, p.Manifest.OutboundHosts)),
            refused = reg.Refused,
        }));

        admin.MapGet("/audit/verify", async (IAuditLog audit) => Results.Ok(await audit.VerifyAsync()));

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

    private static async Task<bool> IsAdminAsync(HttpContext http, KbDbContext db)
    {
        var id = http.User.UserId();
        return await db.Users.AsNoTracking().AnyAsync(u => u.Id == id && u.IsAdmin && u.DeactivatedAt == null);
    }

    private static SpaceDto ToDto(Space s, SpaceRole? role) => new(s.Id, s.Key, s.Name, s.Description, s.IsSensitive, s.IsPublic, role?.ToString());
}
