using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Core.Spaces;
using Kb.Data;
using Kb.Plugins;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;

namespace Kb.Web;

public sealed record DraftDto(string Title, string ContentJson, IReadOnlyList<string> Tags, string BaseVersion, DateTimeOffset UpdatedAt);
public sealed record SaveDraftRequest(string? Title, string ContentJson, string BaseVersion, string[]? Tags = null);
public sealed record VersionContentDto(string Version, string Title, string ContentJson, DateTimeOffset At, string Author);
public sealed record DiffDto(string From, string To, IReadOnlyList<DiffLine> Lines);

public static partial class PageEndpoints
{
    private static readonly TimeSpan DraftLock = TimeSpan.FromHours(24);

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
}
