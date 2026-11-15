using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Core.Spaces;
using Kb.Data;
using Kb.Web.DataCenter;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

// Content on the Data Center compatible surface (contracts/behaviour/content.md, spaces.md, representations.md). Reads shape aerie's pages
// and folders through DcContentService; writes are aerie's own page actions sent from inside, so their checks, plugin hooks and audit apply.
// Aerie holds no blog posts, comments or unpublished drafts (docs/spec-questions/blog-posts-comments-drafts.md): lists of them are empty,
// and creating one is refused with 400.

public sealed record DcJsonPage(IReadOnlyList<JsonObject> Items, bool More);
public sealed record DcCursorPage(IReadOnlyList<JsonObject> Items, string? Cursor, string? Next, string? Prev);

[Action("dc.content.read", Read = true)]
public sealed record DcReadContent : IAction<JsonObject>
{
    public required string Id { get; init; }
    public required IReadOnlyList<string> Statuses { get; init; }
    public int? Version { get; init; }
    public required string Expand { get; init; }
}

[Action("dc.content.list", Read = true)]
public sealed record DcListContent : IAction<DcJsonPage>
{
    public required string Type { get; init; }
    public string? SpaceKey { get; init; }
    public string? Title { get; init; }
    public required IReadOnlyList<string> Statuses { get; init; }
    public IReadOnlyList<string>? Ids { get; init; }
    public required int Start { get; init; }
    public required int Limit { get; init; }
    public required string Expand { get; init; }
}

[Action("dc.content.scan", Read = true)]
public sealed record DcScanContent : IAction<DcCursorPage>
{
    public string? Cursor { get; init; }
    public required string Type { get; init; }
    public string? SpaceKey { get; init; }
    public required IReadOnlyList<string> Statuses { get; init; }
    public required int Limit { get; init; }
    public required string Expand { get; init; }
}

[Action("dc.content.create")]
public sealed record DcCreateContent : IAction<JsonObject>
{
    public required JsonObject Body { get; init; }
    public string? Status { get; init; }
    public required string Expand { get; init; }
}

[Action("dc.content.update")]
public sealed record DcUpdateContent : IAction<JsonObject>
{
    public required string Id { get; init; }
    public required JsonObject Body { get; init; }
    public required string Status { get; init; }
    public required string Expand { get; init; }
}

[Action("dc.content.delete")]
public sealed record DcDeleteContent : IAction<Unit>
{
    public required string Id { get; init; }
    public required string Status { get; init; }
}

[Action("dc.content.history", Read = true)]
public sealed record DcContentHistory : IAction<JsonObject>
{
    public required string Id { get; init; }
    public required string Expand { get; init; }
}

[Action("dc.content.macro", Read = true)]
public sealed record DcReadMacro : IAction<JsonObject>
{
    public required string Id { get; init; }
    public required int Version { get; init; }
    public string? MacroId { get; init; }
    public string? Hash { get; init; }
}

[Action("dc.content.version.remove")]
public sealed record DcRemoveVersion : IAction<Unit>
{
    public required string Id { get; init; }
    public required int Number { get; init; }
}

[Action("dc.content.children", Read = true)]
public sealed record DcContentChildren : IAction<JsonObject>
{
    public required string Id { get; init; }
    public required bool Descendants { get; init; }
    /// <summary>Null for the map of every type; else the one list.</summary>
    public string? Type { get; init; }
    public required int Start { get; init; }
    public required int Limit { get; init; }
    public required string Expand { get; init; }
}

[Action("dc.content.count", Read = true)]
public sealed record DcCountDescendants : IAction<long>
{
    public required string Id { get; init; }
}

[Action("dc.space.content", Read = true)]
public sealed record DcSpaceContent : IAction<JsonObject>
{
    public required string Key { get; init; }
    public string? Type { get; init; }
    public required bool RootOnly { get; init; }
    public required int Start { get; init; }
    public required int Limit { get; init; }
    public required string Expand { get; init; }
}

[Action("dc.space.trash.list", Read = true)]
public sealed record DcListTrash : IAction<DcCursorPage>
{
    public required string Key { get; init; }
    public string? Cursor { get; init; }
    public required int Limit { get; init; }
    public required string Expand { get; init; }
}

[Action("dc.space.trash.empty")]
public sealed record DcEmptyTrash : IAction<Unit>
{
    public required string Key { get; init; }
}

[Action("dc.body.convert", Read = true)]
public sealed record DcConvertBody : IAction<JsonObject>
{
    public required string To { get; init; }
    public string? Value { get; init; }
    public string? Representation { get; init; }
    public string? ContentId { get; init; }
    public required string Expand { get; init; }
}

internal static partial class DcContentRules
{
    public static readonly Problem NotFound = new(404, "No content with this id is visible to you.");
    public static readonly Problem Archived = new(403, "The space is archived.");

    /// <summary>The editor document of a write's body: storage, or the editor form aerie writes (the same markup). Null when there is no body.</summary>
    public static (string? Json, Problem? Problem) Body(JsonObject request)
    {
        if (request["body"] is not JsonObject body) return (null, null);
        foreach (var (name, node) in body)
        {
            if (name is not ("storage" or "editor")) return (null, new Problem(400, $"A body is written as storage or editor, not {name}."));
            if (node is not JsonObject b) continue;
            if (DcRest.Text(b, "representation") is { } r && r != name) return (null, new Problem(400, $"body.{name} has representation {r}."));
            var value = DcRest.Text(b, "value") ?? "";
            if (name == "editor") value = VoidTags().Replace(value, "<$1$2 />");
            try { return (StorageFormat.ToDoc(value).ToJsonString(), null); }
            catch (StorageException e) { return (null, new Problem(400, "The body is not valid storage format: " + e.Message)); }
        }
        return (null, null);
    }

    /// <summary>HTML's empty elements written without a close (&lt;br&gt;), as an editor may send them.</summary>
    [GeneratedRegex(@"<(br|hr|img|col)(\s[^<>]*?)?\s*(?<!/)>", RegexOptions.IgnoreCase)]
    private static partial Regex VoidTags();

    public static string Type(JsonObject body) => DcRest.Text(body, "type") ?? "";

    /// <summary>The last ancestor's id: it names the new parent. Null for none; "" for an empty list (the top of the space).</summary>
    public static string? ParentId(JsonObject body) =>
        body["ancestors"] is not JsonArray a ? null : a.Count == 0 ? "" : a[^1]?["id"] is JsonValue v ? v.ToString() : "-";

    /// <summary>A current page of this title in the space, other than <paramref name="except"/> (titles are unique in a space, without regard to case).</summary>
    public static Task<bool> TitleTakenAsync(KbDbContext db, Guid spaceId, string title, Guid? except) =>
        db.Pages.AnyAsync(p => p.SpaceId == spaceId && p.Id != except && EF.Functions.Like(p.Title, DcContentService.Like(title.Trim()), "\\")); // system: a check, nothing is shown

    /// <summary>"any" means current or trashed. Null when a value is not a status.</summary>
    public static IReadOnlyList<string>? Statuses(IEnumerable<string> asked)
    {
        var list = asked.ToList();
        if (list.Count == 0) return ["current"];
        if (list.Any(s => s is not ("current" or "trashed" or "draft" or "any"))) return null;
        return [.. list.SelectMany(s => s == "any" ? ["current", "trashed"] : new[] { s }).Distinct()];
    }

    public static Problem AsSurface(Problem p) => p.Status == 409 && p.Message is null ? new Problem(409, "The content changed since this version.") : p;
}

internal sealed class DcReadContentHandler(DcContentService content, DcAttachmentService files) : IActionHandler<DcReadContent, JsonObject>
{
    private Page? _page;
    private (Attachment Attachment, Page Page)? _file;

    // An id may name an attachment too: it is content, read here like a page (attachments.md).
    public async Task<Problem?> AuthorizeAsync(ActionContext<DcReadContent> ctx)
    {
        if ((_page = await content.FindAsync(ctx.Actor, ctx.Action.Id, ctx.Action.Statuses, track: false, ctx.Cancel)) is not null) return null;
        _file = await files.FindAsync(ctx.Actor, ctx.Action.Id, null, trashedToo: ctx.Action.Statuses.Contains("trashed"), ctx.Cancel);
        return _file is { } f && (f.Attachment.DeletedAt is null ? ctx.Action.Statuses.Contains("current") : ctx.Action.Statuses.Contains("trashed")) ? null : DcContentRules.NotFound;
    }

    public async Task<Outcome<JsonObject>> HandleAsync(ActionContext<DcReadContent> ctx)
    {
        if (_file is { } file) return await files.JsonAsync(ctx.Actor, file.Attachment, file.Page, Expand.Parse(ctx.Action.Expand), ctx.Cancel, top: true);
        var page = _page!;
        if (ctx.Action.Version is { } n && (await content.VersionsAsync(page.Id, ctx.Cancel)).All(v => v.Number != n) && !(n == 1 && page.Kind == PageKind.Folder))
            return new Problem(404, "No such version.");
        return await content.ContentAsync(ctx.Actor, page, Expand.Parse(ctx.Action.Expand), ctx.Cancel, ctx.Action.Version, top: true);
    }
}

internal sealed class DcListContentHandler(KbDbContext db, IAccess access, DcContentService content) : IActionHandler<DcListContent, DcJsonPage>
{
    private Guid? _space;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcListContent> ctx)
    {
        if (ctx.Action.SpaceKey is not { } key) return null;
        _space = (await DcSpaceData.FindAsync(db, access, ctx.Actor, key, ctx.Cancel))?.Space.Id;
        return _space is null ? new Problem(404, "No space with this key is visible to you.") : null;
    }

    public async Task<Outcome<DcJsonPage>> HandleAsync(ActionContext<DcListContent> ctx)
    {
        var req = ctx.Action;
        // With ids, type is not applied.
        if (req.Ids is null && req.Type != "page") return new DcJsonPage([], false);
        var scope = await content.ScopeAsync(ctx.Actor, ctx.Cancel);
        if (_space is { } only) scope = new PageScope([.. scope.SpaceIds.Where(s => s == only)]);
        var (trashed, current) = (req.Statuses.Contains("trashed"), req.Statuses.Contains("current"));
        var query = db.Pages.IgnoreQueryFilters().AsNoTracking().In(scope).Where(p => !p.BinHidden)
            .Where(p => p.DeletedAt == null ? current : trashed);
        if (req.Title is { } title) query = query.Where(p => EF.Functions.Like(p.Title, DcContentService.Like(title), "\\"));
        if (req.Ids is { } ids)
        {
            var wanted = new List<Guid>();
            foreach (var id in ids) if (await content.AerieIdAsync(id, ctx.Cancel) is { } g) wanted.Add(g);
            query = query.Where(p => wanted.Contains(p.Id));
        }
        await content.EnsureIdsAsync(ctx.Cancel);
        var rows = await query.Join(db.DcContents, p => p.Id, d => d.AerieId, (p, d) => new { Page = p, d.Id }).OrderBy(x => x.Id).ToListAsync(ctx.Cancel);
        // A trashed page is listed only to whoever manages its space's bin.
        var bins = new Dictionary<Guid, bool>();
        var visible = new List<Page>();
        foreach (var r in rows)
        {
            if (r.Page.DeletedAt is not null)
            {
                if (!bins.TryGetValue(r.Page.SpaceId, out var manages)) bins[r.Page.SpaceId] = manages = await access.CanAsync(ctx.Actor, r.Page.SpaceId, SpaceAction.ManageBinAgingComments, ctx.Cancel);
                if (!manages) continue;
            }
            visible.Add(r.Page);
        }
        var expand = Expand.Parse(req.Expand);
        var items = new List<JsonObject>();
        foreach (var p in visible.Skip(req.Start).Take(req.Limit)) items.Add(await content.ContentAsync(ctx.Actor, p, expand, ctx.Cancel));
        return new DcJsonPage(items, visible.Count > req.Start + req.Limit);
    }
}

internal sealed class DcScanContentHandler(KbDbContext db, IAccess access, DcContentService content) : IActionHandler<DcScanContent, DcCursorPage>
{
    private Guid? _space;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcScanContent> ctx)
    {
        if (ctx.Action.SpaceKey is not { } key) return null;
        _space = (await DcSpaceData.FindAsync(db, access, ctx.Actor, key, ctx.Cancel))?.Space.Id;
        return _space is null ? new Problem(404, "No space with this key is visible to you.") : null;
    }

    public async Task<Outcome<DcCursorPage>> HandleAsync(ActionContext<DcScanContent> ctx)
    {
        var req = ctx.Action;
        // The cursor holds the type and where the page ends: "type|n|id" walks on after id, "type|p|id" back before it.
        var (type, forward, from) = (req.Type, true, 0L);
        if (req.Cursor is { } cursor)
        {
            if (DcCursor.Read(cursor) is not [var t, var d, var n] || d is not ("n" or "p") || !long.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out from))
                return new Problem(400, "The cursor is not valid.");
            (type, forward) = (t, d == "n");
        }
        if (type != "page") return new DcCursorPage([], req.Cursor, null, null);
        var scope = await content.ScopeAsync(ctx.Actor, ctx.Cancel);
        if (_space is { } only) scope = new PageScope([.. scope.SpaceIds.Where(s => s == only)]);
        await content.EnsureIdsAsync(ctx.Cancel);
        var trashed = req.Statuses.Contains("trashed");
        var manages = new List<Guid>();
        if (trashed) foreach (var s in scope.SpaceIds) if (await access.CanAsync(ctx.Actor, s, SpaceAction.ManageBinAgingComments, ctx.Cancel)) manages.Add(s);
        var current = req.Statuses.Contains("current");
        var rows = db.Pages.IgnoreQueryFilters().AsNoTracking().In(scope).Where(p => !p.BinHidden)
            .Where(p => p.DeletedAt == null ? current : trashed && manages.Contains(p.SpaceId))
            .Join(db.DcContents, p => p.Id, d => d.AerieId, (p, d) => new { Page = p, d.Id });
        var page = forward
            ? await rows.Where(x => x.Id > from).OrderBy(x => x.Id).Take(req.Limit + 1).ToListAsync(ctx.Cancel)
            : (await rows.Where(x => x.Id < from).OrderByDescending(x => x.Id).Take(req.Limit + 1).ToListAsync(ctx.Cancel));
        var more = page.Count > req.Limit;
        page = [.. page.Take(req.Limit)];
        if (!forward) page.Reverse();
        var expand = Expand.Parse(req.Expand);
        var items = new List<JsonObject>();
        foreach (var r in page) items.Add(await content.ContentAsync(ctx.Actor, r.Page, expand, ctx.Cancel));
        var hasNext = forward ? more : page.Count > 0;
        var hasPrev = forward ? from > 0 && page.Count > 0 : more;
        return new DcCursorPage(items, req.Cursor,
            hasNext && page.Count > 0 ? DcCursor.Write(type, "n", page[^1].Id) : null,
            hasPrev && page.Count > 0 ? DcCursor.Write(type, "p", page[0].Id) : null);
    }
}

/// <summary>Opaque cursors: the fields joined and written as URL-safe base64.</summary>
internal static class DcCursor
{
    public static string Write(params object[] parts) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('|', parts.Select(p => Convert.ToString(p, CultureInfo.InvariantCulture))))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string[]? Read(string cursor)
    {
        try
        {
            var b = cursor.Replace('-', '+').Replace('_', '/');
            b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(b)).Split('|');
        }
        catch (FormatException) { return null; }
    }
}

internal sealed class DcCreateContentHandler(KbDbContext db, IAccess access, DcContentService content, IActionBus bus, TimeProvider time) : IActionHandler<DcCreateContent, JsonObject>
{
    private (Kb.Core.Spaces.Space Space, Kb.Core.DataCenter.DcSpace Dc)? _space;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcCreateContent> ctx)
    {
        var body = ctx.Action.Body;
        if (ctx.Actor.UserId is null) return Problem.Unauthorized;
        if (DcContentRules.Type(body) is var type && type != "page")
            return new Problem(400, type is "blogpost" or "comment" ? $"Aerie has no {type}s." : type == "attachment" ? "Attachments are added with the child attachment operation." : "type is page.");
        if (DcRest.Text(body, "space.key") is not { Length: > 0 } key) return new Problem(400, "space.key is required.");
        if ((_space = await DcSpaceData.FindAsync(db, access, ctx.Actor, key, ctx.Cancel)) is null) return new Problem(404, "No space with this key is visible to you.");
        if (_space.Value.Dc.ArchivedAt is not null) return DcContentRules.Archived;
        return await access.CanAsync(ctx.Actor, _space.Value.Space.Id, SpaceAction.CreateEdit, ctx.Cancel) ? null : new Problem(403, "You may not add content in this space.");
    }

    public async Task<Outcome<JsonObject>> HandleAsync(ActionContext<DcCreateContent> ctx)
    {
        var (req, space, me) = (ctx.Action, _space!.Value.Space, ctx.Actor.UserId!.Value);
        // The status query parameter wins over the body's.
        if ((req.Status ?? DcRest.Text(req.Body, "status") ?? "current") is var status && status != "current")
            return new Problem(400, status == "draft" ? "Aerie has no unpublished drafts of new content." : "status is current or draft.");
        if (DcRest.Text(req.Body, "title") is not { } raw || raw.Trim().Length == 0) return new Problem(400, "title is required.");
        var title = raw.Trim();
        if (title.Length > 200) return new Problem(400, "A title is at most 200 characters in aerie.");
        if (await DcContentRules.TitleTakenAsync(db, space.Id, title, null)) return new Problem(400, "A page with this title already exists in the space.");
        var (json, invalid) = DcContentRules.Body(req.Body);
        if (invalid is not null) return invalid;

        Guid? parentId = null;
        if (DcContentRules.ParentId(req.Body) is { Length: > 0 } ancestor)
        {
            if (await content.FindAsync(ctx.Actor, ancestor, ["current", "trashed"], track: false, ctx.Cancel) is not { } parent) return new Problem(404, "No parent with this id is visible to you.");
            if (parent.SpaceId != space.Id || parent.DeletedAt is not null) return new Problem(400, "The parent must be a current page in the same space.");
            if (await content.FoldAsync(parent, me, time.GetUtcNow()))
                ctx.Audit(AuditActions.PageToFolder, "page", parent.Id.ToString(), space.Id, new Dictionary<string, string> { ["title"] = parent.Title });
            parentId = parent.Id;
        }
        var created = await bus.SendAsync(new CreatePage { SpaceId = space.Id, ParentId = parentId, Kind = "Page", Title = title, ContentJson = json }, CancellationToken.None);
        if (created.Problem is { } p) return p;
        var page = await db.Pages.AsNoTracking().FirstAsync(x => x.Id == created.Value!.Id, CancellationToken.None); // system: the page just made
        return await content.ContentAsync(ctx.Actor, page, Expand.Parse(req.Expand), CancellationToken.None, top: true);
    }
}

internal sealed class DcUpdateContentHandler(KbDbContext db, IAccess access, DcContentService content, IActionBus bus, TimeProvider time) : IActionHandler<DcUpdateContent, JsonObject>
{
    private Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcUpdateContent> ctx)
    {
        if (ctx.Actor.UserId is null) return Problem.Unauthorized;
        if (ctx.Action.Status is not ("current" or "trashed")) return DcContentRules.NotFound; // aerie has no drafts
        if ((_page = await content.FindAsync(ctx.Actor, ctx.Action.Id, [ctx.Action.Status], track: false, ctx.Cancel)) is null) return DcContentRules.NotFound;
        if (await content.ArchivedAsync(_page.SpaceId, ctx.Cancel)) return DcContentRules.Archived;
        return await access.CanAsync(ctx.Actor, _page.SpaceId, SpaceAction.CreateEdit, ctx.Cancel) ? null : new Problem(403, "You may not edit this content.");
    }

    public async Task<Outcome<JsonObject>> HandleAsync(ActionContext<DcUpdateContent> ctx)
    {
        var (req, page, me) = (ctx.Action, _page!, ctx.Actor.UserId!.Value);
        var body = req.Body;
        if (DcContentRules.Type(body) != "page") return new Problem(400, "type must be the content's type, page.");
        var expand = Expand.Parse(req.Expand);
        // A trashed page comes back with status=trashed and a body saying current. Restoring makes no version.
        if (req.Status == "trashed")
        {
            if (DcRest.Text(body, "status") != "current") return new Problem(400, "A trashed page is restored with status current.");
            var restored = await bus.SendAsync(new RestorePage { PageId = page.DeleteRootId ?? page.Id }, CancellationToken.None);
            if (restored.Problem is { } rp) return rp.Status == 404 ? new Problem(409, "This page comes back only with the folder it was trashed with.") : rp;
            return await content.ContentAsync(ctx.Actor, await db.Pages.AsNoTracking().FirstAsync(x => x.Id == page.Id, CancellationToken.None), expand, CancellationToken.None, top: true); // system: the page just restored
        }
        if (body["version"]?["number"] is not JsonValue nv || !nv.TryGetValue<int>(out var number)) return new Problem(400, "version.number is required.");
        var versions = await content.VersionsAsync(page.Id, ctx.Cancel);
        var current = DcContentService.Current(page, versions);
        if (number != current.Number + 1) return new Problem(409, $"version.number must be {current.Number + 1}, one more than the current version.");
        var title = DcRest.Text(body, "title")?.Trim() ?? page.Title;
        if (title.Length is 0 or > 200) return new Problem(400, "A title is 1 to 200 characters in aerie.");
        if (!string.Equals(title, page.Title, StringComparison.OrdinalIgnoreCase) && await DcContentRules.TitleTakenAsync(db, page.SpaceId, title, page.Id))
            return new Problem(400, "A page with this title already exists in the space.");
        var (json, invalid) = DcContentRules.Body(body);
        if (invalid is not null) return invalid;
        var minor = body["version"]?["minorEdit"] is JsonValue mv && mv.TryGetValue<bool>(out var m) && m;
        var message = DcRest.Text(body, "version.message");

        // ancestors moves the page in its space; a parent that is a page becomes a folder first.
        if (DcContentRules.ParentId(body) is { } ancestor)
        {
            Guid? parentId = null;
            if (ancestor.Length > 0)
            {
                if (await content.FindAsync(ctx.Actor, ancestor, ["current"], track: false, ctx.Cancel) is not { } parent || parent.SpaceId != page.SpaceId)
                    return new Problem(400, "The parent must be a current page in the same space.");
                if (parent.Id == page.Id || (await content.DescendantsAsync(page, ctx.Cancel)).Any(d => d.Id == parent.Id)) return new Problem(400, "A page cannot move under itself.");
                if (await content.FoldAsync(parent, me, time.GetUtcNow()))
                    ctx.Audit(AuditActions.PageToFolder, "page", parent.Id.ToString(), page.SpaceId, new Dictionary<string, string> { ["title"] = parent.Title });
                parentId = parent.Id;
            }
            if (parentId != page.ParentId)
            {
                var moved = await bus.SendAsync(new MovePage { PageId = page.Id, ParentId = parentId }, CancellationToken.None);
                if (moved.Problem is { } mp) return mp;
            }
        }

        if (page.Kind == PageKind.Page)
        {
            var saved = await bus.SendAsync(new SavePage { PageId = page.Id, Title = title, ContentJson = json ?? page.ContentJson, BaseVersion = page.Version, Major = !minor, Note = message }, CancellationToken.None);
            if (saved.Problem is { } sp) return DcContentRules.AsSurface(sp);
        }
        else
        {
            // A folder: aerie's own save only renames it. Its body and its versions are kept here (content.md: a folder's body is its own content).
            if (title != page.Title)
            {
                var renamed = await bus.SendAsync(new SavePage { PageId = page.Id, Title = title, ContentJson = page.ContentJson, BaseVersion = page.Version }, CancellationToken.None);
                if (renamed.Problem is { } rp) return DcContentRules.AsSurface(rp);
            }
            var folder = await db.Pages.FirstAsync(x => x.Id == page.Id, CancellationToken.None); // system: the folder the caller may edit, checked above
            var now = time.GetUtcNow();
            if (versions.Count == 0) // the version the surface showed as 1 becomes a real one first
                db.PageVersions.Add(new PageVersion { PageId = folder.Id, Major = folder.Major, Minor = folder.Minor, Title = folder.Title, ContentJson = folder.ContentJson, AuthorId = folder.CreatedBy, At = folder.CreatedAt, Source = "api" });
            if (minor) folder.Minor++; else { folder.Major++; folder.Minor = 0; }
            folder.ContentJson = json ?? folder.ContentJson;
            (folder.UpdatedBy, folder.UpdatedAt) = (me, now);
            db.PageVersions.Add(new PageVersion { PageId = folder.Id, Major = folder.Major, Minor = folder.Minor, Title = folder.Title, ContentJson = folder.ContentJson, AuthorId = me, At = now, Note = message, Source = "api" });
            try { await db.SaveChangesAsync(CancellationToken.None); }
            catch (DbUpdateException) { return new Problem(409, "The content changed since this version."); }
        }
        var fresh = await db.Pages.AsNoTracking().FirstAsync(x => x.Id == page.Id, CancellationToken.None); // system: the page just saved
        return await content.ContentAsync(ctx.Actor, fresh, expand, CancellationToken.None, top: true);
    }
}

internal sealed class DcDeleteContentHandler(KbDbContext db, IAccess access, DcContentService content, IActionBus bus) : IActionHandler<DcDeleteContent, Unit>
{
    private Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcDeleteContent> ctx)
    {
        if (ctx.Actor.UserId is null) return Problem.Unauthorized;
        // The status is the one the content has now: a trashed page is purged only when the request says trashed.
        if (ctx.Action.Status is not ("current" or "trashed")) return DcContentRules.NotFound;
        if ((_page = await content.FindAsync(ctx.Actor, ctx.Action.Id, [ctx.Action.Status], track: false, ctx.Cancel)) is null) return DcContentRules.NotFound;
        if (await content.ArchivedAsync(_page.SpaceId, ctx.Cancel)) return DcContentRules.Archived;
        var allowed = ctx.Action.Status == "trashed"
            ? await access.CanAsync(ctx.Actor, _page.SpaceId, SpaceAction.ManageBinAgingComments, ctx.Cancel)
            : await PageSupport.CanRemoveAsync(access, ctx.Actor, _page.SpaceId, [_page]);
        return allowed ? null : new Problem(403, "You may not remove this content.");
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<DcDeleteContent> ctx)
    {
        var page = _page!;
        if (ctx.Action.Status == "trashed")
        {
            // Purged from the space's trash: what aerie's "empty bin" does, for this one item. The admin bin keeps it.
            await db.Pages.IgnoreQueryFilters().Where(p => p.Id == page.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.BinHidden, true), CancellationToken.None); // system: the caller manages this bin, checked above
            ctx.Audit(AuditActions.PagePurge, "page", page.Id.ToString(), page.SpaceId, new Dictionary<string, string> { ["title"] = page.Title });
            return new Unit();
        }
        // Child pages are not trashed: they move up to the trashed page's parent first.
        foreach (var child in await content.ChildrenAsync(page, ctx.Cancel))
        {
            var moved = await bus.SendAsync(new MovePage { PageId = child.Id, ParentId = page.ParentId }, CancellationToken.None);
            if (moved.Problem is { } mp) return mp;
        }
        return await bus.SendAsync(new DeletePage { PageId = page.Id }, CancellationToken.None);
    }
}

internal sealed class DcContentHistoryHandler(DcContentService content) : IActionHandler<DcContentHistory, JsonObject>
{
    private Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcContentHistory> ctx) =>
        (_page = await content.FindAsync(ctx.Actor, ctx.Action.Id, ["current", "trashed"], track: false, ctx.Cancel)) is null ? DcContentRules.NotFound : null;

    public async Task<Outcome<JsonObject>> HandleAsync(ActionContext<DcContentHistory> ctx)
    {
        var versions = await content.VersionsAsync(_page!.Id, ctx.Cancel);
        var current = DcContentService.Current(_page, versions);
        return content.Links.Top(await content.HistoryAsync(ctx.Actor, _page, versions, current, Expand.Parse(ctx.Action.Expand), ctx.Cancel));
    }
}

internal sealed class DcReadMacroHandler(DcContentService content) : IActionHandler<DcReadMacro, JsonObject>
{
    private Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcReadMacro> ctx) =>
        (_page = await content.FindAsync(ctx.Actor, ctx.Action.Id, ["current", "trashed"], track: false, ctx.Cancel)) is null ? DcContentRules.NotFound : null;

    public async Task<Outcome<JsonObject>> HandleAsync(ActionContext<DcReadMacro> ctx)
    {
        var req = ctx.Action;
        var versions = await content.VersionsAsync(_page!.Id, ctx.Cancel);
        var v = versions.FirstOrDefault(x => x.Number == req.Version) ?? (req.Version == 1 && versions.Count == 0 ? DcContentService.Current(_page, versions) : null);
        if (v is null) return new Problem(404, "No such version.");
        var macros = StorageFormat.Macros(StorageFormat.Parse(StorageFormat.FromDoc(v.ContentJson)));
        // The hash is aerie's own: lower-case hex SHA-256 of the macro's storage markup (content.md, Macro bodies).
        var macro = req.MacroId is { } id ? macros.FirstOrDefault(m => m.Attr("ac:macro-id") == id)
            : macros.FirstOrDefault(m => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(m.Raw))) == req.Hash);
        if (macro is null) return new Problem(404, "No such macro in this version.");
        var (name, body, parameters) = StorageFormat.Describe(macro);
        var p = new JsonObject();
        foreach (var (k, value) in parameters) p[k] = value;
        return new JsonObject { ["name"] = name, ["body"] = body, ["parameters"] = p };
    }
}

internal sealed class DcRemoveVersionHandler(IAccess access, DcContentService content) : IActionHandler<DcRemoveVersion, Unit>
{
    private Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcRemoveVersion> ctx)
    {
        if (ctx.Actor.UserId is null) return Problem.Unauthorized;
        if ((_page = await content.FindAsync(ctx.Actor, ctx.Action.Id, ["current"], track: false, ctx.Cancel)) is null) return DcContentRules.NotFound;
        if (await content.ArchivedAsync(_page.SpaceId, ctx.Cancel)) return DcContentRules.Archived;
        return await access.CanAsync(ctx.Actor, _page.SpaceId, SpaceAction.CreateEdit, ctx.Cancel) ? null : new Problem(403, "You may not edit this content.");
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<DcRemoveVersion> ctx)
    {
        var versions = await content.VersionsAsync(_page!.Id, ctx.Cancel);
        var current = DcContentService.Current(_page, versions);
        if (ctx.Action.Number == current.Number) return new Problem(400, "The current version cannot be removed.");
        if (versions.FirstOrDefault(v => v.Number == ctx.Action.Number) is not { } v) return new Problem(404, "No such version.");
        // Aerie's history is append-only: the version leaves the surface, and stays in aerie's own history and audit trail.
        await content.MarkRemovedAsync(v.RowId);
        ctx.Audit(AuditActions.PageVersionRemove, "page", _page.Id.ToString(), _page.SpaceId,
            new Dictionary<string, string> { ["number"] = v.Number.ToString(CultureInfo.InvariantCulture), ["version"] = $"{v.Major}.{v.Minor}" });
        return new Unit();
    }
}

internal sealed class DcContentChildrenHandler(DcContentService content) : IActionHandler<DcContentChildren, JsonObject>
{
    private Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcContentChildren> ctx) =>
        (_page = await content.FindAsync(ctx.Actor, ctx.Action.Id, ["current"], track: false, ctx.Cancel)) is null ? DcContentRules.NotFound : null;

    public async Task<Outcome<JsonObject>> HandleAsync(ActionContext<DcContentChildren> ctx)
    {
        var (req, page) = (ctx.Action, _page!);
        var paging = new Paging(req.Start, req.Limit);
        var expand = Expand.Parse(req.Expand);
        if (req.Type is null) return content.Links.Top(await content.ByTypeAsync(ctx.Actor, page, expand, req.Descendants, paging, ctx.Cancel));
        if (req.Type == "attachment")
        {
            var files = ctx.Services.GetRequiredService<DcAttachmentService>();
            var at = $"/rest/api/content/{await content.IdOfAsync(page.Id, ctx.Cancel)}/{(req.Descendants ? "descendant" : "child")}/attachment";
            return await files.PageOfAsync(ctx.Actor, page, await files.OfPageAsync(page.Id, ctx.Cancel), paging, expand, at, ctx.Cancel);
        }
        // Comments and blog posts: aerie has none.
        var items = req.Type == "page" ? (req.Descendants ? await content.DescendantsAsync(page, ctx.Cancel) : await content.ChildrenAsync(page, ctx.Cancel)) : [];
        var path = $"/rest/api/content/{await content.IdOfAsync(page.Id, ctx.Cancel)}/{(req.Descendants ? "descendant" : "child")}/{req.Type}";
        var o = await content.PageOfAsync(ctx.Actor, items, paging, expand, path, ctx.Cancel);
        return o;
    }
}

internal sealed class DcCountDescendantsHandler(DcContentService content) : IActionHandler<DcCountDescendants, long>
{
    private Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcCountDescendants> ctx) =>
        (_page = await content.FindAsync(ctx.Actor, ctx.Action.Id, ["current"], track: false, ctx.Cancel)) is null ? DcContentRules.NotFound : null;

    public async Task<Outcome<long>> HandleAsync(ActionContext<DcCountDescendants> ctx) => (long)(await content.DescendantsAsync(_page!, ctx.Cancel)).Count;
}

internal sealed class DcSpaceContentHandler(KbDbContext db, IAccess access, DcContentService content) : IActionHandler<DcSpaceContent, JsonObject>
{
    private Guid _space;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcSpaceContent> ctx)
    {
        if (await DcSpaceData.FindAsync(db, access, ctx.Actor, ctx.Action.Key, ctx.Cancel) is not { } found) return new Problem(404, "No space with this key is visible to you.");
        _space = found.Space.Id;
        return null;
    }

    public async Task<Outcome<JsonObject>> HandleAsync(ActionContext<DcSpaceContent> ctx)
    {
        var req = ctx.Action;
        var scope = new PageScope([_space]);
        await content.EnsureIdsAsync(ctx.Cancel);
        var pages = await db.Pages.AsNoTracking().In(scope).Where(p => !req.RootOnly || p.ParentId == null)
            .Join(db.DcContents, p => p.Id, d => d.AerieId, (p, d) => new { Page = p, d.Id }).OrderBy(x => x.Id).Select(x => x.Page).ToListAsync(ctx.Cancel);
        var key = DcSpaceJson.Key(await content.SpaceAsync(_space, ctx.Cancel));
        var path = "/rest/api/space/" + Uri.EscapeDataString(key) + "/content";
        var paging = new Paging(req.Start, req.Limit);
        var expand = Expand.Parse(req.Expand);
        if (req.Type is { } type) return await content.PageOfAsync(ctx.Actor, type == "page" ? pages : [], paging, expand, path + "/" + type, ctx.Cancel);
        return content.Links.Top(new JsonObject
        {
            ["page"] = await content.PageOfAsync(ctx.Actor, pages, paging, expand.Inside("page"), path + "/page", ctx.Cancel),
            ["blogpost"] = await content.PageOfAsync(ctx.Actor, [], paging, expand.Inside("blogpost"), path + "/blogpost", ctx.Cancel),
            ["_links"] = new JsonObject { ["self"] = content.Links.Abs(path) },
        });
    }
}

internal sealed class DcListTrashHandler(KbDbContext db, IAccess access, DcContentService content) : IActionHandler<DcListTrash, DcCursorPage>
{
    private Guid _space;

    // The trash is aerie's recycle bin, managed by those with its bin right (Supervisor and up).
    public async Task<Problem?> AuthorizeAsync(ActionContext<DcListTrash> ctx)
    {
        if (ctx.Actor.UserId is null) return Problem.Unauthorized;
        if (await DcSpaceData.FindAsync(db, access, ctx.Actor, ctx.Action.Key, ctx.Cancel) is not { } found) return new Problem(404, "No space with this key is visible to you.");
        _space = found.Space.Id;
        return await access.CanAsync(ctx.Actor, _space, SpaceAction.ManageBinAgingComments, ctx.Cancel) ? null : new Problem(403, "You may not manage this space's trash.");
    }

    public async Task<Outcome<DcCursorPage>> HandleAsync(ActionContext<DcListTrash> ctx)
    {
        var req = ctx.Action;
        var scope = new PageScope([_space]);
        await content.EnsureIdsAsync(ctx.Cancel);
        // Newest trashed first; the cursor holds the last item's place (when, then id), so no item shows twice.
        var all = (await db.Pages.IgnoreQueryFilters().AsNoTracking().In(scope).Where(p => p.DeletedAt != null && !p.BinHidden)
                .Join(db.DcContents, p => p.Id, d => d.AerieId, (p, d) => new { Page = p, d.Id }).ToListAsync(ctx.Cancel))
            .OrderByDescending(x => x.Page.DeletedAt).ThenBy(x => x.Id).ToList();
        var from = 0;
        if (req.Cursor is { } cursor)
        {
            if (DcCursor.Read(cursor) is not ["trash", var ticks, var id] || !long.TryParse(ticks, NumberStyles.None, CultureInfo.InvariantCulture, out var t)
                || !long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var last)) return new Problem(400, "The cursor is not valid.");
            from = all.FindIndex(x => x.Page.DeletedAt!.Value.UtcTicks < t || (x.Page.DeletedAt!.Value.UtcTicks == t && x.Id > last));
            if (from < 0) from = all.Count;
        }
        var page = all.Skip(from).Take(req.Limit).ToList();
        var expand = Expand.Parse(req.Expand);
        var items = new List<JsonObject>();
        foreach (var x in page) items.Add(await content.ContentAsync(ctx.Actor, x.Page, expand, ctx.Cancel));
        string Mark(int i) => DcCursor.Write("trash", all[i].Page.DeletedAt!.Value.UtcTicks, all[i].Id);
        var next = from + page.Count < all.Count && page.Count > 0 ? Mark(from + page.Count - 1) : null;
        // A cursor that points just before the first item of the previous page.
        var prev = from > 0 ? from - req.Limit - 1 >= 0 ? Mark(from - req.Limit - 1) : DcCursor.Write("trash", long.MaxValue, 0) : null;
        return new DcCursorPage(items, req.Cursor, next, prev);
    }
}

internal sealed class DcEmptyTrashHandler(KbDbContext db, IAccess access, IActionBus bus) : IActionHandler<DcEmptyTrash, Unit>
{
    private Guid _space;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcEmptyTrash> ctx)
    {
        if (ctx.Actor.UserId is null) return Problem.Unauthorized;
        if (await DcSpaceData.FindAsync(db, access, ctx.Actor, ctx.Action.Key, ctx.Cancel) is not { } found) return new Problem(404, "No space with this key is visible to you.");
        _space = found.Space.Id;
        return await access.CanAsync(ctx.Actor, _space, SpaceAction.ManageBinAgingComments, ctx.Cancel) ? null : new Problem(403, "You may not manage this space's trash.");
    }

    // Aerie's own empty bin: the space's trash shows nothing more; the admin bin keeps it.
    public Task<Outcome<Unit>> HandleAsync(ActionContext<DcEmptyTrash> ctx) => bus.SendAsync(new EmptyBin { SpaceId = _space }, CancellationToken.None);
}

internal sealed class DcConvertBodyHandler(DcContentService content) : IActionHandler<DcConvertBody, JsonObject>
{
    private Page? _context;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcConvertBody> ctx)
    {
        if (ctx.Actor.UserId is null) return Problem.Unauthorized;
        // A page named as context must be one the caller can view; else the conversion runs on no page.
        if (ctx.Action.ContentId is { } id) _context = await content.FindAsync(ctx.Actor, id, ["current"], track: false, ctx.Cancel);
        return null;
    }

    public async Task<Outcome<JsonObject>> HandleAsync(ActionContext<DcConvertBody> ctx)
    {
        var req = ctx.Action;
        var from = req.Representation;
        var pair = (from, req.To) is ("storage", "view" or "export_view" or "styled_view" or "editor") or ("editor", "storage");
        if (!pair) return new Problem(400, $"Aerie converts storage to view, export_view, styled_view or editor, and editor to storage; not {from} to {req.To}.");
        var (json, invalid) = DcContentRules.Body(new JsonObject { ["body"] = new JsonObject { [from!] = new JsonObject { ["value"] = req.Value ?? "", ["representation"] = from } } });
        if (invalid is not null) return invalid;
        var storage = StorageFormat.FromDoc(json);
        var value = req.To == "storage" ? storage : await content.RenderAsync(ctx.Actor, _context, from == "storage" ? req.Value ?? "" : storage, _context?.Title ?? "", req.To, ctx.Cancel);
        var o = new JsonObject { ["value"] = value, ["representation"] = req.To };
        if (_context is not null && Expand.Parse(req.Expand).Has("content")) o["content"] = await content.ContentAsync(ctx.Actor, _context, Expand.None, ctx.Cancel);
        return o;
    }
}
