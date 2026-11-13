using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Core.Spaces;
using Kb.Data;
using Kb.Plugins;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;

namespace Kb.Web;

public sealed record TreeNodeDto(Guid Id, Guid? ParentId, string Kind, string Title, int Sort);
public sealed record CreatePageRequest(Guid? ParentId, PageKind Kind, string Title, string[]? Tags = null, string? ContentJson = null);
public sealed record SavePageRequest(string Title, string ContentJson, string BaseVersion, bool Major = false, string? Note = null, string[]? Tags = null);
public sealed record CrumbDto(Guid Id, string Title);
public sealed record VersionDto(string Version, DateTimeOffset At, string Author, string? Note);
public sealed record DraftInfoDto(bool Mine, string By, DateTimeOffset UpdatedAt);
public sealed record AttachmentDto(Guid Id, string Name, long Size, string Url, DateTimeOffset At);
public sealed record PageDto(Guid Id, Guid SpaceId, Guid? ParentId, string Kind, string Title, string ContentJson, string Version,
    DateTimeOffset UpdatedAt, string UpdatedBy, IReadOnlyList<CrumbDto> Path, IReadOnlyList<VersionDto> History, bool CanEdit, IReadOnlyList<string> Tags,
    bool CanDelete, DraftInfoDto? Draft, IReadOnlyList<AttachmentDto> Attachments, long MaxAttachmentBytes);

public static partial class PageEndpoints
{
    public static void MapPageEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/spaces/{id:guid}/tree", async (Guid id, HttpContext http, ISpacePermissionService perms, KbDbContext db) =>
        {
            if (await perms.GetRoleAsync(http.User.UserId(), id) is null) return Results.NotFound();
            var nodes = await db.Pages.AsNoTracking().Where(p => p.SpaceId == id).OrderBy(p => p.Sort)
                .Select(p => new TreeNodeDto(p.Id, p.ParentId, p.Kind.ToString(), p.Title, p.Sort)).ToListAsync();
            return Results.Ok(nodes);
        });

        api.MapPost("/spaces/{id:guid}/pages", async (Guid id, CreatePageRequest req, HttpContext http, ISpacePermissionService perms, KbDbContext db, TimeProvider time) =>
        {
            var me = http.User.UserId();
            if (await perms.GetRoleAsync(me, id) is null) return Results.NotFound();
            if (!await perms.CanAsync(me, id, SpaceAction.CreateEdit)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (TitleError(req.Title) is { } te) return te;
            if (CleanTags(req.Tags) is not { } tags) return TagError();
            if (req.Kind == PageKind.Page && req.ContentJson is not null && !IsEditorDoc(req.ContentJson))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["contentJson"] = ["Not an editor document."] });
            if (req.ParentId is { } pid && !await db.Pages.AnyAsync(p => p.Id == pid && p.SpaceId == id && p.Kind == PageKind.Folder))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["parentId"] = ["Must be a folder in this space."] });

            var now = time.GetUtcNow();
            var page = new Page { SpaceId = id, ParentId = req.ParentId, Kind = req.Kind, Title = req.Title.Trim(), Sort = await NextSortAsync(db, id, req.ParentId), CreatedBy = me, CreatedAt = now, UpdatedBy = me, UpdatedAt = now };
            if (page.Kind == PageKind.Page) page.ContentJson = req.ContentJson ?? """{"type":"doc","content":[]}""";
            db.Pages.Add(page);
            db.PageTags.AddRange(tags.Select(t => new PageTag { PageId = page.Id, Tag = t }));
            if (page.Kind == PageKind.Page)
                db.PageVersions.Add(new PageVersion { PageId = page.Id, Major = 1, Minor = 0, Title = page.Title, ContentJson = page.ContentJson, AuthorId = me, At = now });
            await SearchIndex.StageAsync(db, page, tags);
            await db.SaveChangesAsync();
            await ImageEndpoints.KeepAsync(db, page.ContentJson);
            return Results.Created($"/api/v1/pages/{page.Id}", await ToDtoAsync(db, perms, page, me));
        });

        api.MapGet("/pages/{id:guid}", async (Guid id, HttpContext http, ISpacePermissionService perms, KbDbContext db) =>
        {
            var me = http.User.UserId();
            var page = await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
            if (page is null || await perms.GetRoleAsync(me, page.SpaceId) is null) return Results.NotFound();
            return Results.Ok(await ToDtoAsync(db, perms, page, me));
        });

        api.MapPut("/pages/{id:guid}", async (Guid id, SavePageRequest req, HttpContext http, ISpacePermissionService perms, KbDbContext db, TimeProvider time) =>
        {
            var me = http.User.UserId();
            var page = await db.Pages.FirstOrDefaultAsync(p => p.Id == id);
            if (page is null || await perms.GetRoleAsync(me, page.SpaceId) is null) return Results.NotFound();
            if (!await perms.CanAsync(me, page.SpaceId, SpaceAction.CreateEdit)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (TitleError(req.Title) is { } te) return te;
            if (CleanTags(req.Tags) is not { } newTags) return TagError();
            if (page.Kind == PageKind.Page && !IsEditorDoc(req.ContentJson))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["contentJson"] = ["Not an editor document."] });
            // Someone else saved since this editor loaded: refuse rather than overwrite.
            if (req.BaseVersion != page.Version) return Results.Conflict(new { current = page.Version });

            page.Title = req.Title.Trim();
            if (req.Tags is not null)
            {
                db.PageTags.RemoveRange(db.PageTags.Where(t => t.PageId == page.Id));
                db.PageTags.AddRange(newTags.Select(t => new PageTag { PageId = page.Id, Tag = t }));
            }
            else newTags = await db.PageTags.Where(t => t.PageId == page.Id).Select(t => t.Tag).ToListAsync();
            return await PublishAsync(db, perms, page, me, req.ContentJson, req.Major, req.Note, newTags, time.GetUtcNow());
        });

        MapMoveDelete(api);
        MapVersions(api);
        MapDrafts(api);
    }
}
