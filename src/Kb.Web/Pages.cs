using System.Globalization;
using System.Text.Json;
using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Core.Spaces;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

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
public sealed record MoveRequest(Guid? SpaceId, Guid? ParentId);
public sealed record BinItemDto(Guid Id, Guid SpaceId, string SpaceName, string Kind, string Title, string Location, int Items, DateTimeOffset DeletedAt, string DeletedBy, bool Hidden);
public sealed record DraftDto(string Title, string ContentJson, IReadOnlyList<string> Tags, string BaseVersion, DateTimeOffset UpdatedAt);
public sealed record SaveDraftRequest(string? Title, string ContentJson, string BaseVersion, string[]? Tags = null);
public sealed record VersionContentDto(string Version, string Title, string ContentJson, DateTimeOffset At, string Author);
public sealed record DiffDto(string From, string To, IReadOnlyList<DiffLine> Lines);

public static class PageEndpoints
{
    private const int MaxContentChars = 5_000_000;
    private static readonly TimeSpan DraftLock = TimeSpan.FromHours(24);

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

    /// <summary>Writes a new version of a page (or renames a folder), re-indexes it and clears the author's draft.</summary>
    private static async Task<IResult> PublishAsync(KbDbContext db, ISpacePermissionService perms, Page page, Guid me, string contentJson, bool major, string? note, IEnumerable<string> tags, DateTimeOffset now)
    {
        page.UpdatedBy = me;
        page.UpdatedAt = now;
        if (page.Kind == PageKind.Page)
        {
            page.ContentJson = contentJson;
            if (major) { page.Major++; page.Minor = 0; } else page.Minor++;
            db.PageVersions.Add(new PageVersion { PageId = page.Id, Major = page.Major, Minor = page.Minor, Title = page.Title, ContentJson = page.ContentJson, AuthorId = me, At = now, Note = note });
        }
        db.PageDrafts.RemoveRange(db.PageDrafts.Where(d => d.PageId == page.Id && d.UserId == me));
        await SearchIndex.StageAsync(db, page, tags);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { return Results.Conflict(); } // concurrent save took this version number
        await ImageEndpoints.KeepAsync(db, page.ContentJson);
        return Results.Ok(await ToDtoAsync(db, perms, page, me));
    }

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

    private static void MapVersions(RouteGroupBuilder api)
    {
        api.MapGet("/pages/{id:guid}/versions", async (Guid id, HttpContext http, ISpacePermissionService perms, KbDbContext db) =>
        {
            var page = await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
            if (page is null || await perms.GetRoleAsync(http.User.UserId(), page.SpaceId) is null) return Results.NotFound();
            return Results.Ok(await HistoryAsync(db, id, int.MaxValue));
        });

        api.MapGet("/pages/{id:guid}/versions/{version}", async (Guid id, string version, HttpContext http, ISpacePermissionService perms, KbDbContext db) =>
        {
            var page = await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
            if (page is null || await perms.GetRoleAsync(http.User.UserId(), page.SpaceId) is null) return Results.NotFound();
            if (await VersionAsync(db, id, version) is not { } v) return Results.NotFound();
            var author = await db.Users.Where(u => u.Id == v.AuthorId).Select(u => u.DisplayName).FirstOrDefaultAsync() ?? "Unknown";
            return Results.Ok(new VersionContentDto(version, v.Title, v.ContentJson, v.At, author));
        });

        api.MapGet("/pages/{id:guid}/diff", async (Guid id, string from, string to, HttpContext http, ISpacePermissionService perms, KbDbContext db) =>
        {
            var page = await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
            if (page is null || await perms.GetRoleAsync(http.User.UserId(), page.SpaceId) is null) return Results.NotFound();
            if (await VersionAsync(db, id, from) is not { } a || await VersionAsync(db, id, to) is not { } b) return Results.NotFound();
            static List<string> Lines(PageVersion v) => [$"# {v.Title}", "", .. DocText.MarkdownLines(v.ContentJson)];
            return Results.Ok(new DiffDto(from, to, LineDiff.Diff(Lines(a), Lines(b))));
        });

        // Restoring never rewrites history: the old content becomes a new version.
        api.MapPost("/pages/{id:guid}/versions/{version}/restore", async (Guid id, string version, HttpContext http, ISpacePermissionService perms, KbDbContext db, IAuditLog audit, TimeProvider time) =>
        {
            var me = http.User.UserId();
            var page = await db.Pages.FirstOrDefaultAsync(p => p.Id == id);
            if (page is null || await perms.GetRoleAsync(me, page.SpaceId) is null) return Results.NotFound();
            if (!await perms.CanAsync(me, page.SpaceId, SpaceAction.CreateEdit)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (await VersionAsync(db, id, version) is not { } v) return Results.NotFound();
            page.Title = v.Title;
            var tags = await db.PageTags.Where(t => t.PageId == id).Select(t => t.Tag).ToListAsync();
            var result = await PublishAsync(db, perms, page, me, v.ContentJson, major: false, $"Restored from {version}", tags, time.GetUtcNow());
            await audit.AppendAsync(new AuditEvent(AuditActions.PageVersionRestore, me, "page", id.ToString(), page.SpaceId, new Dictionary<string, string> { ["from"] = version, ["to"] = page.Version }));
            return result;
        });
    }

    private static void MapDrafts(RouteGroupBuilder api)
    {
        api.MapGet("/pages/{id:guid}/draft", async (Guid id, HttpContext http, KbDbContext db) =>
        {
            var me = http.User.UserId();
            var d = await db.PageDrafts.AsNoTracking().FirstOrDefaultAsync(x => x.PageId == id && x.UserId == me);
            return d is null ? Results.NotFound() : Results.Ok(new DraftDto(d.Title, d.ContentJson, d.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries), d.BaseVersion, d.UpdatedAt));
        });

        api.MapPut("/pages/{id:guid}/draft", async (Guid id, SaveDraftRequest req, HttpContext http, ISpacePermissionService perms, KbDbContext db, TimeProvider time) =>
        {
            var me = http.User.UserId();
            var page = await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.Kind == PageKind.Page);
            if (page is null || await perms.GetRoleAsync(me, page.SpaceId) is null) return Results.NotFound();
            if (!await perms.CanAsync(me, page.SpaceId, SpaceAction.CreateEdit)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (CleanTags(req.Tags) is not { } tags) return TagError();
            var title = (req.Title ?? "").Trim();
            if (title.Length > 200 || !IsEditorDoc(req.ContentJson) || req.BaseVersion is not { Length: > 0 and <= 24 })
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["draft"] = ["Invalid draft."] });

            var now = time.GetUtcNow();
            var d = await db.PageDrafts.FirstOrDefaultAsync(x => x.PageId == id);
            // One draft per page. Someone else's stays theirs unless it has sat untouched for a day.
            if (d is not null && d.UserId != me && d.UpdatedAt > now - DraftLock) return Results.Conflict();
            if (d is null) db.PageDrafts.Add(d = new PageDraft { PageId = id, Title = title, ContentJson = req.ContentJson, BaseVersion = req.BaseVersion });
            d.UserId = me; d.Title = title; d.ContentJson = req.ContentJson; d.BaseVersion = req.BaseVersion; d.Tags = string.Join(',', tags); d.UpdatedAt = now;
            try { await db.SaveChangesAsync(); } catch (DbUpdateException) { return Results.Conflict(); }
            await ImageEndpoints.KeepAsync(db, req.ContentJson);
            return Results.Ok(new { updatedAt = now });
        });

        api.MapDelete("/pages/{id:guid}/draft", async (Guid id, HttpContext http, KbDbContext db) =>
        {
            var me = http.User.UserId();
            await db.PageDrafts.Where(x => x.PageId == id && x.UserId == me).ExecuteDeleteAsync();
            return Results.NoContent();
        });
    }

    private static async Task<int> NextSortAsync(KbDbContext db, Guid spaceId, Guid? parentId) =>
        (await db.Pages.Where(p => p.SpaceId == spaceId && p.ParentId == parentId).MaxAsync(p => (int?)p.Sort) ?? 0) + 1;

    /// <summary>The item and everything under it, deleted items included, tracked for update.</summary>
    private static async Task<List<Page>> SubtreeAsync(KbDbContext db, Page root)
    {
        var links = await db.Pages.IgnoreQueryFilters().Where(p => p.SpaceId == root.SpaceId).Select(p => new { p.Id, p.ParentId }).ToListAsync();
        var children = links.Where(l => l.ParentId is not null).ToLookup(l => l.ParentId!.Value, l => l.Id);
        var ids = new List<Guid> { root.Id };
        for (var i = 0; i < ids.Count; i++) ids.AddRange(children[ids[i]]);
        return await db.Pages.IgnoreQueryFilters().Where(p => ids.Contains(p.Id)).ToListAsync();
    }

    /// <summary>Contributors move and delete their own work; other people's needs a Supervisor.</summary>
    private static Task<bool> CanRemoveAsync(ISpacePermissionService perms, Guid me, Guid spaceId, IEnumerable<Page> items) =>
        perms.CanAsync(me, spaceId, items.All(p => p.CreatedBy == me) ? SpaceAction.MoveDeleteOwn : SpaceAction.MoveDeleteOthers);

    private static async Task<List<BinItemDto>> BinAsync(KbDbContext db, Guid? spaceId, bool includeHidden)
    {
        var roots = await db.Pages.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.DeletedAt != null && p.DeleteRootId == p.Id && (spaceId == null || p.SpaceId == spaceId) && (includeHidden || !p.BinHidden))
            .OrderByDescending(p => p.DeletedAt).Take(500)
            .Select(p => new { p.Id, p.SpaceId, p.Kind, p.Title, p.ParentId, p.DeletedAt, p.DeletedBy, p.BinHidden }).ToListAsync();
        var rootIds = roots.Select(r => r.Id).ToList();
        var spaceIds = roots.Select(r => r.SpaceId).Distinct().ToList();
        var counts = await db.Pages.IgnoreQueryFilters().Where(p => p.DeleteRootId != null && rootIds.Contains(p.DeleteRootId.Value))
            .GroupBy(p => p.DeleteRootId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.N);
        var spaces = await db.Spaces.AsNoTracking().Where(s => spaceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name);
        var folders = await db.Pages.IgnoreQueryFilters().AsNoTracking().Where(p => spaceIds.Contains(p.SpaceId) && p.Kind == PageKind.Folder)
            .Select(p => new { p.Id, p.ParentId, p.Title }).ToDictionaryAsync(p => p.Id);
        var userIds = roots.Select(r => r.DeletedBy).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);

        return roots.Select(r =>
        {
            var path = new List<string>();
            var parent = r.ParentId;
            for (var i = 0; parent is { } pid && i < 50 && folders.TryGetValue(pid, out var f); i++) { path.Insert(0, f.Title); parent = f.ParentId; }
            var space = spaces.GetValueOrDefault(r.SpaceId, "");
            return new BinItemDto(r.Id, r.SpaceId, space, r.Kind.ToString(), r.Title, string.Join(" / ", path.Prepend(space)), counts.GetValueOrDefault(r.Id, 1),
                r.DeletedAt!.Value, r.DeletedBy is { } by ? users.GetValueOrDefault(by, "Unknown") : "Unknown", r.BinHidden);
        }).ToList();
    }

    private static async Task<PageVersion?> VersionAsync(KbDbContext db, Guid pageId, string version)
    {
        var parts = version.Split('.');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)) return null;
        return await db.PageVersions.AsNoTracking().FirstOrDefaultAsync(v => v.PageId == pageId && v.Major == major && v.Minor == minor);
    }

    private static async Task<List<VersionDto>> HistoryAsync(KbDbContext db, Guid pageId, int take)
    {
        var rows = await (from v in db.PageVersions.AsNoTracking()
                          where v.PageId == pageId
                          orderby v.Major descending, v.Minor descending
                          join u in db.Users on v.AuthorId equals u.Id into au
                          from u in au.DefaultIfEmpty()
                          select new { v.Major, v.Minor, v.At, Author = u == null ? "Unknown" : u.DisplayName, v.Note })
                         .Take(take).ToListAsync();
        return rows.Select(h => new VersionDto($"{h.Major}.{h.Minor}", h.At, h.Author, h.Note)).ToList();
    }

    private static IResult? TitleError(string? title) =>
        string.IsNullOrWhiteSpace(title) || title.Trim().Length > 200
            ? Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["1-200 characters."] })
            : null;

    /// <summary>Trimmed, de-duplicated (case-insensitive) tags; null when any tag is invalid.</summary>
    private static List<string>? CleanTags(string[]? tags)
    {
        var list = (tags ?? []).Select(t => t?.Trim() ?? "").Where(t => t.Length > 0).DistinctBy(t => t.ToLowerInvariant()).ToList();
        return list.Count <= 20 && list.All(t => t.Length <= 50 && !t.Contains(',')) ? list.Select(t => t.ToLowerInvariant()).ToList() : null;
    }

    private static IResult TagError() =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["tags"] = ["Up to 20 tags, 1-50 characters each, no commas."] });

    private static bool IsEditorDoc(string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > MaxContentChars) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "doc";
        }
        catch (JsonException) { return false; }
    }

    internal static async Task<PageDto> ToDtoAsync(KbDbContext db, ISpacePermissionService perms, Page page, Guid me)
    {
        var path = new List<CrumbDto>();
        var parentId = page.ParentId;
        for (var i = 0; parentId is { } pid && i < 50; i++)
        {
            var parent = await db.Pages.IgnoreQueryFilters().AsNoTracking().Where(p => p.Id == pid).Select(p => new { p.Id, p.Title, p.ParentId }).FirstAsync();
            path.Insert(0, new CrumbDto(parent.Id, parent.Title));
            parentId = parent.ParentId;
        }

        var tags = await db.PageTags.AsNoTracking().Where(t => t.PageId == page.Id).OrderBy(t => t.Tag).Select(t => t.Tag).ToListAsync();
        var updatedBy = await db.Users.Where(u => u.Id == page.UpdatedBy).Select(u => u.DisplayName).FirstOrDefaultAsync() ?? "Unknown";
        var draft = await (from d in db.PageDrafts.AsNoTracking()
                           where d.PageId == page.Id
                           join u in db.Users on d.UserId equals u.Id
                           select new { d.UserId, u.DisplayName, d.UpdatedAt }).FirstOrDefaultAsync();
        var files = await db.Attachments.AsNoTracking().Where(a => a.PageId == page.Id && a.DeletedAt == null).OrderBy(a => a.FileName)
            .Select(a => new AttachmentDto(a.Id, a.FileName, a.Size, "/api/v1/attachments/" + a.Id, a.CreatedAt)).ToListAsync();
        var canEdit = await perms.CanAsync(me, page.SpaceId, SpaceAction.CreateEdit);
        // Folders: whether their contents allow it is checked on the delete itself.
        var canDelete = await perms.CanAsync(me, page.SpaceId, page.CreatedBy == me ? SpaceAction.MoveDeleteOwn : SpaceAction.MoveDeleteOthers);

        return new PageDto(page.Id, page.SpaceId, page.ParentId, page.Kind.ToString(), page.Title, page.ContentJson, page.Version,
            page.UpdatedAt, updatedBy, path, await HistoryAsync(db, page.Id, 10), canEdit, tags, canDelete,
            draft is null ? null : new DraftInfoDto(draft.UserId == me, draft.DisplayName, draft.UpdatedAt), files, await UploadLimits.AttachmentBytesAsync(db));
    }
}
