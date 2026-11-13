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

public sealed record SpaceDto(Guid Id, string Key, string Name, string? Description, bool IsSensitive, bool IsPublic, string? MyRole,
    bool CommentsEnabled, int AgingDays, string AgingAudience, int? RecycleDays, DateTimeOffset CreatedAt);
public sealed record CreateSpaceRequest(string Name, string? Key, string? Description, bool IsSensitive = false, bool IsPublic = false);
public sealed record UpdateSpaceRequest(string? Name, string? Description, bool? IsSensitive, bool? IsPublic,
    bool? CommentsEnabled = null, int? AgingDays = null, AgingAudience? AgingAudience = null, int? RecycleDays = null);
public sealed record RoleAssignmentDto(Guid Id, PrincipalType PrincipalType, string PrincipalId, SpaceRole Role);
public sealed record GrantRoleRequest(PrincipalType PrincipalType, string PrincipalId, SpaceRole Role);

public static partial class EndpointMappings
{
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
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Required."] });
            var key = req.Key;
            if (string.IsNullOrWhiteSpace(key))
            {
                // Key is internal (URLs use IDs). Derive from the name and make it unique.
                var stem = NonKeyChars().Replace(req.Name.Trim().ToLowerInvariant(), "-").Trim('-');
                stem = stem.Length < 2 ? "space" : stem[..Math.Min(stem.Length, 28)].TrimEnd('-');
                key = stem;
                for (var n = 2; await db.Spaces.AnyAsync(x => x.Key == key); n++) key = $"{stem}-{n}";
            }
            else if (!SpaceKeyPattern().IsMatch(key)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["key"] = ["2-32 chars: lowercase letters, digits, hyphen."] });
            else if (await db.Spaces.AnyAsync(x => x.Key == key)) return Results.Conflict();
            var s = new Space { Key = key, Name = req.Name.Trim(), Description = req.Description, IsSensitive = req.IsSensitive, IsPublic = req.IsPublic, CreatedAt = time.GetUtcNow() };
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
            return await UpdateSpaceAsync(s, req, await IsAdminAsync(http, db), me, db, audit) ?? Results.Ok(ToDto(s, await perms.GetRoleAsync(me, id)));
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
}
