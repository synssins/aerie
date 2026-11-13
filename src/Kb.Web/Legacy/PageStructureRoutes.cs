using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Core.Spaces;
using Kb.Data;
using Kb.Plugins;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;

namespace Kb.Web;

public sealed record MoveRequest(Guid? SpaceId, Guid? ParentId);
public sealed record BinItemDto(Guid Id, Guid SpaceId, string SpaceName, string Kind, string Title, string Location, int Items, DateTimeOffset DeletedAt, string DeletedBy, bool Hidden);

public static partial class PageEndpoints
{
    private static void MapMoveDelete(RouteGroupBuilder api)
    {
        api.MapPost("/pages/{id:guid}/move", async (Guid id, MoveRequest req, HttpContext http, ISpacePermissionService perms, KbDbContext db, IAuditLog audit) =>
        {
            var me = http.User.UserId();
            var page = await db.Pages.FirstOrDefaultAsync(p => p.Id == id);
            if (page is null || await perms.GetRoleAsync(me, page.SpaceId) is null) return Results.NotFound();
            var from = page.SpaceId;
            var target = req.SpaceId ?? from;
            if (await perms.GetRoleAsync(me, target) is null) return Results.NotFound();
            if (!await perms.CanAsync(me, target, SpaceAction.CreateEdit)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var subtree = await SubtreeAsync(db, page);
            if (!await CanRemoveAsync(perms, me, from, subtree)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (req.ParentId is { } pid)
            {
                if (subtree.Any(p => p.Id == pid)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["parentId"] = ["A folder can't move into itself."] });
                if (!await db.Pages.AnyAsync(p => p.Id == pid && p.SpaceId == target && p.Kind == PageKind.Folder))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["parentId"] = ["Must be a folder in the target space."] });
            }
            if (page.ParentId == req.ParentId && target == from) return Results.Ok(await ToDtoAsync(db, perms, page, me));

            page.Sort = await NextSortAsync(db, target, req.ParentId);
            page.ParentId = req.ParentId;
            if (target != from)
            {
                // Images are stored per space. Let the target space show the ones these pages use, now and in their history.
                var ids = subtree.Select(p => p.Id).ToList();
                var docs = subtree.Select(p => p.ContentJson).Concat(await db.PageVersions.Where(v => ids.Contains(v.PageId)).Select(v => v.ContentJson).ToListAsync());
                var images = docs.SelectMany(DocText.ImageIds).Distinct().ToList();
                var granted = await db.SpaceImageGrants.Where(g => g.SpaceId == target && images.Contains(g.ImageId)).Select(g => g.ImageId).ToListAsync();
                var foreign = await db.SpaceImages.Where(i => images.Contains(i.Id) && i.SpaceId != target).Select(i => i.Id).ToListAsync();
                db.SpaceImageGrants.AddRange(foreign.Except(granted).Select(i => new SpaceImageGrant { ImageId = i, SpaceId = target }));
                foreach (var p in subtree) p.SpaceId = target;
            }
            await db.SaveChangesAsync();
            await audit.AppendAsync(new AuditEvent(AuditActions.PageMove, me, "page", page.Id.ToString(), target,
                new Dictionary<string, string> { ["fromSpace"] = from.ToString(), ["parent"] = req.ParentId?.ToString() ?? "" }));
            return Results.Ok(await ToDtoAsync(db, perms, page, me));
        });

        // Delete = move to the recycle bin. Nothing here destroys data.
        api.MapDelete("/pages/{id:guid}", async (Guid id, HttpContext http, ISpacePermissionService perms, KbDbContext db, IAuditLog audit, TimeProvider time) =>
        {
            var me = http.User.UserId();
            var page = await db.Pages.FirstOrDefaultAsync(p => p.Id == id);
            if (page is null || await perms.GetRoleAsync(me, page.SpaceId) is null) return Results.NotFound();
            var live = (await SubtreeAsync(db, page)).Where(p => p.DeletedAt is null).ToList();
            if (!await CanRemoveAsync(perms, me, page.SpaceId, live)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var now = time.GetUtcNow();
            foreach (var p in live) { p.DeletedAt = now; p.DeletedBy = me; p.DeleteRootId = page.Id; p.BinHidden = false; }
            await db.SaveChangesAsync();
            await audit.AppendAsync(new AuditEvent(AuditActions.PageDelete, me, "page", page.Id.ToString(), page.SpaceId,
                new Dictionary<string, string> { ["title"] = page.Title, ["items"] = live.Count.ToString(CultureInfo.InvariantCulture) }));
            return Results.NoContent();
        });

        api.MapGet("/spaces/{id:guid}/bin", async (Guid id, HttpContext http, ISpacePermissionService perms, KbDbContext db) =>
        {
            var me = http.User.UserId();
            if (await perms.GetRoleAsync(me, id) is null) return Results.NotFound();
            if (!await perms.CanAsync(me, id, SpaceAction.ManageBinAgingComments)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(await BinAsync(db, id, includeHidden: false));
        });

        // Hides the items from the space's bin. The admin bin keeps them.
        api.MapPost("/spaces/{id:guid}/bin/empty", async (Guid id, HttpContext http, ISpacePermissionService perms, KbDbContext db, IAuditLog audit) =>
        {
            var me = http.User.UserId();
            if (await perms.GetRoleAsync(me, id) is null) return Results.NotFound();
            if (!await perms.CanAsync(me, id, SpaceAction.ManageBinAgingComments)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var n = await db.Pages.IgnoreQueryFilters().Where(p => p.SpaceId == id && p.DeletedAt != null && !p.BinHidden).ExecuteUpdateAsync(s => s.SetProperty(p => p.BinHidden, true));
            await audit.AppendAsync(new AuditEvent(AuditActions.BinEmpty, me, "space", id.ToString(), id, new Dictionary<string, string> { ["items"] = n.ToString(CultureInfo.InvariantCulture) }));
            return Results.NoContent();
        });

        api.MapPost("/pages/{id:guid}/restore", async (Guid id, HttpContext http, ISpacePermissionService perms, KbDbContext db, IAuditLog audit) =>
        {
            var me = http.User.UserId();
            var root = await db.Pages.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt != null && p.DeleteRootId == p.Id);
            if (root is null) return Results.NotFound();
            if (!await db.IsAdminAsync(me))
            {
                if (root.BinHidden || await perms.GetRoleAsync(me, root.SpaceId) is null) return Results.NotFound();
                if (!await perms.CanAsync(me, root.SpaceId, SpaceAction.ManageBinAgingComments)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var deleted = await db.Pages.IgnoreQueryFilters().Where(p => p.SpaceId == root.SpaceId && p.DeletedAt != null).ToListAsync();
            var byId = deleted.ToDictionary(p => p.Id);
            var oldRoot = deleted.ToDictionary(p => p.Id, p => p.DeleteRootId);
            static void Undelete(Page p) { p.DeletedAt = null; p.DeletedBy = null; p.DeleteRootId = null; p.BinHidden = false; }

            foreach (var p in deleted.Where(p => p.DeleteRootId == root.Id)) Undelete(p);
            // Deleted parent folders come back as empty shells: the folder chain only, never their other contents.
            for (var parent = root.ParentId; parent is { } pid && byId.TryGetValue(pid, out var folder) && folder.DeletedAt is not null; parent = folder.ParentId) Undelete(folder);
            // Whatever stays deleted under a folder that came back becomes its own bin entry.
            foreach (var p in deleted.Where(p => p.DeletedAt is not null && !(p.DeleteRootId is { } r && byId.TryGetValue(r, out var rp) && rp.DeletedAt is not null)))
            {
                var top = p;
                while (top.ParentId is { } up && byId.TryGetValue(up, out var a) && a.DeletedAt is not null && oldRoot[a.Id] == oldRoot[p.Id]) top = a;
                p.DeleteRootId = top.Id;
            }

            if (await db.Pages.AnyAsync(p => p.SpaceId == root.SpaceId && p.ParentId == root.ParentId && p.Kind == root.Kind && p.Title == root.Title))
            {
                const string suffix = " (restored)";
                root.Title = root.Title[..Math.Min(root.Title.Length, 200 - suffix.Length)] + suffix;
                await SearchIndex.StageAsync(db, root, await db.PageTags.Where(t => t.PageId == root.Id).Select(t => t.Tag).ToListAsync());
            }
            await db.SaveChangesAsync();
            await audit.AppendAsync(new AuditEvent(AuditActions.PageRestore, me, "page", root.Id.ToString(), root.SpaceId, new Dictionary<string, string> { ["title"] = root.Title }));
            return Results.Ok(new { id = root.Id, spaceId = root.SpaceId });
        });

        api.AdminGroup().MapGet("/bin", async (KbDbContext db) => Results.Ok(await BinAsync(db, null, includeHidden: true)));
    }
}
