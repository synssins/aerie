using System.Globalization;
using System.Text.Json.Nodes;
using Kb.Core.Config;
using Kb.Core.Content;
using Kb.Core.DataCenter;
using Kb.Core.Identity;
using Kb.Core.Spaces;
using Kb.Data;
using Kb.Web.Actions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kb.Web.DataCenter;

/// <summary>A version of a page as the surface numbers it (DcVersion).</summary>
public sealed record DcPageVersion(long RowId, int Number, int Major, int Minor, string Title, string ContentJson, Guid AuthorId, DateTimeOffset At, string? Note);

/// <summary>
/// Pages and folders on the Data Center compatible surface (contracts/behaviour/content.md). Both are `type: page`: a folder's children are its
/// child pages, a page has none (How the page tree maps onto aerie's tree). Every page query here is limited to the caller's visible spaces.
/// </summary>
internal sealed class DcContentService(KbDbContext db, IAccess access, IHttpContextAccessor http, IOptions<KbOptions> options)
{
    public DcLinks Links => DcLinks.Of(http.HttpContext!);
    private KbDbContext Db => db;
    private IAccess Access => access;
    private string Icon => options.Value.Brand.LogoLightPath;

    // ---- ids ----

    /// <summary>Gives every page, folder and attachment without one its compatible id, in creation order (as DcSpaceData does for spaces).</summary>
    public async Task EnsureIdsAsync(CancellationToken ct)
    {
        var pages = await db.Pages.IgnoreQueryFilters().Where(p => !db.DcContents.Any(d => d.AerieId == p.Id)).Select(p => new { p.Id, p.CreatedAt, Kind = "page" }).ToListAsync(ct); // system: ids for every page, shown to no one
        var files = await db.Attachments.Where(a => !db.DcContents.Any(d => d.AerieId == a.Id)).Select(a => new { a.Id, a.CreatedAt, Kind = "attachment" }).ToListAsync(ct);
        var missing = pages.Concat(files).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToList();
        if (missing.Count == 0) return;
        var rows = missing.Select(x => new DcContent { AerieId = x.Id, Kind = x.Kind }).ToList();
        db.DcContents.AddRange(rows);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { foreach (var r in rows) db.Entry(r).State = EntityState.Detached; }
    }

    public async Task<long> IdOfAsync(Guid aerieId, CancellationToken ct)
    {
        var id = await db.DcContents.Where(d => d.AerieId == aerieId).Select(d => (long?)d.Id).FirstOrDefaultAsync(ct);
        if (id is { } found) return found;
        await EnsureIdsAsync(ct);
        return await db.DcContents.Where(d => d.AerieId == aerieId).Select(d => d.Id).FirstAsync(ct);
    }

    public async Task<Dictionary<Guid, long>> IdsOfAsync(IReadOnlyCollection<Guid> aerieIds, CancellationToken ct)
    {
        if (await db.DcContents.CountAsync(d => aerieIds.Contains(d.AerieId), ct) < aerieIds.Count) await EnsureIdsAsync(ct);
        return await db.DcContents.Where(d => aerieIds.Contains(d.AerieId)).ToDictionaryAsync(d => d.AerieId, d => d.Id, ct);
    }

    /// <summary>The aerie id behind a content id the client sent: decimal digits, or the aerie UUID itself.</summary>
    public async Task<Guid?> AerieIdAsync(string? id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (Guid.TryParse(id, out var uuid)) return uuid;
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return null;
        await EnsureIdsAsync(ct);
        return await db.DcContents.Where(d => d.Id == n).Select(d => (Guid?)d.AerieId).FirstOrDefaultAsync(ct);
    }

    /// <summary>A LIKE pattern matching exactly this text, without regard to case on every database (as aerie's people search does).</summary>
    public static string Like(string text) => text.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal)
        .Replace("_", @"\_", StringComparison.Ordinal).Replace("[", @"\[", StringComparison.Ordinal);

    // ---- finding pages ----

    public async Task<PageScope> ScopeAsync(Actor actor, CancellationToken ct) => new(await access.VisibleSpacesAsync(actor, ct));

    /// <summary>
    /// The page or folder under this id when the caller may see it in one of these statuses, else null (404). A trashed one is seen only by
    /// whoever manages its space's bin, as in aerie; what a bin was emptied of is gone.
    /// </summary>
    public async Task<Page?> FindAsync(Actor actor, string? id, IReadOnlyCollection<string> statuses, bool track, CancellationToken ct)
    {
        if (await AerieIdAsync(id, ct) is not { } aerie) return null;
        var query = track ? db.Pages.IgnoreQueryFilters() : db.Pages.IgnoreQueryFilters().AsNoTracking();
        var page = await query.FirstOrDefaultAsync(p => p.Id == aerie && !p.BinHidden, ct); // system: the role check follows
        if (page is null || await access.RoleAsync(actor, page.SpaceId, ct) is null) return null;
        var trashed = page.DeletedAt is not null;
        if (trashed ? !statuses.Contains("trashed") : !statuses.Contains("current")) return null;
        if (trashed && !await access.CanAsync(actor, page.SpaceId, SpaceAction.ManageBinAgingComments, ct)) return null;
        return page;
    }

    public Task<bool> ArchivedAsync(Guid spaceId, CancellationToken ct) => db.DcSpaces.AnyAsync(d => d.SpaceId == spaceId && d.ArchivedAt != null, ct);

    /// <summary>A page that gains a child becomes a folder in place, keeping its id, title, body, versions, labels and attachments.</summary>
    public async Task<bool> FoldAsync(Page parent, Guid by, DateTimeOffset now)
    {
        if (parent.Kind == PageKind.Folder) return false;
        var tracked = await db.Pages.FirstAsync(p => p.Id == parent.Id); // system: the caller may edit this space, checked before
        tracked.Kind = PageKind.Folder;
        (tracked.UpdatedBy, tracked.UpdatedAt) = (by, now);
        await db.SaveChangesAsync();
        return true;
    }

    // ---- versions ----

    /// <summary>The page's versions with their compatible numbers, oldest first, without those removed through the surface.</summary>
    public async Task<List<DcPageVersion>> VersionsAsync(Guid pageId, CancellationToken ct)
    {
        var rows = await db.PageVersions.AsNoTracking().Where(v => v.PageId == pageId).OrderBy(v => v.Major).ThenBy(v => v.Minor).ThenBy(v => v.Id).ToListAsync(ct);
        var numbers = await db.DcVersions.Where(n => n.PageId == pageId).ToDictionaryAsync(n => n.PageVersionId, ct);
        var missing = rows.Where(v => !numbers.ContainsKey(v.Id)).ToList();
        if (missing.Count > 0)
        {
            var next = numbers.Count == 0 ? 0 : numbers.Values.Max(n => n.Number);
            var added = missing.Select(v => new DcVersion { PageVersionId = v.Id, PageId = pageId, Number = ++next }).ToList();
            db.DcVersions.AddRange(added);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { foreach (var a in added) db.Entry(a).State = EntityState.Detached; }
            numbers = await db.DcVersions.Where(n => n.PageId == pageId).ToDictionaryAsync(n => n.PageVersionId, ct);
        }
        return [.. rows.Where(v => numbers.TryGetValue(v.Id, out var n) && !n.Removed)
            .Select(v => new DcPageVersion(v.Id, numbers[v.Id].Number, v.Major, v.Minor, v.Title, v.ContentJson, v.AuthorId, v.At, v.Note)).OrderBy(v => v.Number)];
    }

    /// <summary>The version the page is at now. A folder made in aerie has none: it shows as version 1 of what it holds.</summary>
    public static DcPageVersion Current(Page p, IReadOnlyList<DcPageVersion> versions) =>
        versions.LastOrDefault(v => v.Major == p.Major && v.Minor == p.Minor) ?? (versions.Count > 0 ? versions[^1] : null)
        ?? new DcPageVersion(0, 1, p.Major, p.Minor, p.Title, p.ContentJson, p.CreatedBy, p.CreatedAt, null);

    public Task MarkRemovedAsync(long rowId) => db.DcVersions.Where(v => v.PageVersionId == rowId).ExecuteUpdateAsync(s => s.SetProperty(v => v.Removed, true));

    // ---- shaping ----

    private readonly Dictionary<Guid, DcUser?> _users = [];

    private async Task<DcUser?> UserAsync(Guid id, CancellationToken ct)
    {
        if (_users.TryGetValue(id, out var known)) return known;
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return _users[id] = u is null ? null : DcUserData.View(u);
    }

    private async Task<JsonNode?> UserJsonAsync(Guid id, Expand expand, CancellationToken ct) =>
        await UserAsync(id, ct) is { } u ? DcUserJson.Of(u, Links, expand) : DcUserJson.Of(new DcUser(id, "", "", true, true), Links, expand);

    private readonly Dictionary<Guid, DcSpaceView> _spaces = [];

    public async Task<DcSpaceView> SpaceAsync(Guid spaceId, CancellationToken ct)
    {
        if (_spaces.TryGetValue(spaceId, out var known)) return known;
        await DcSpaceData.EnsureIdsAsync(db, ct);
        var s = await db.Spaces.AsNoTracking().FirstAsync(x => x.Id == spaceId, ct);
        var dc = await db.DcSpaces.AsNoTracking().FirstAsync(x => x.SpaceId == spaceId, ct);
        return _spaces[spaceId] = await DcSpaceData.ViewAsync(db, s, dc, ct);
    }

    public async Task<JsonObject> SpaceJsonAsync(Guid spaceId, Expand expand, CancellationToken ct) => DcSpaceJson.Of(await SpaceAsync(spaceId, ct), Links, expand, Icon);

    private string Self(long id) => Links.Abs("/rest/api/content/" + id.ToString(CultureInfo.InvariantCulture));

    /// <summary>id, type, status, title and links: how ancestors and other short mentions show content.</summary>
    public async Task<JsonObject> ShortAsync(Page p, CancellationToken ct)
    {
        var id = await IdOfAsync(p.Id, ct);
        return new JsonObject
        {
            ["id"] = id.ToString(CultureInfo.InvariantCulture), ["type"] = "page", ["status"] = p.DeletedAt is null ? "current" : "trashed", ["title"] = p.Title,
            ["_links"] = new JsonObject { ["self"] = Self(id), ["webui"] = "/p/" + p.Id, ["tinyui"] = "/p/" + p.Id },
        };
    }

    /// <summary>The content object, with what <paramref name="expand"/> asks for.</summary>
    /// <param name="atVersion">The number of an older version to show (status historical), or null for now.</param>
    public async Task<JsonObject> ContentAsync(Actor actor, Page p, Expand expand, CancellationToken ct, int? atVersion = null, bool top = false)
    {
        var id = await IdOfAsync(p.Id, ct);
        var versions = await VersionsAsync(p.Id, ct);
        var current = Current(p, versions);
        var shown = atVersion is { } n ? versions.FirstOrDefault(v => v.Number == n) ?? current : current;
        var historical = shown.Number != current.Number;
        var canEdit = await access.CanAsync(actor, p.SpaceId, SpaceAction.CreateEdit, ct);
        var o = new JsonObject
        {
            ["id"] = id.ToString(CultureInfo.InvariantCulture),
            ["type"] = "page",
            ["status"] = p.DeletedAt is not null ? "trashed" : historical ? "historical" : "current",
            ["title"] = historical ? shown.Title : p.Title,
            ["extensions"] = new JsonObject { ["position"] = p.Sort },
        };
        var space = await SpaceAsync(p.SpaceId, ct);
        var spacePath = "/rest/api/space/" + Uri.EscapeDataString(DcSpaceJson.Key(space));
        var self = "/rest/api/content/" + id.ToString(CultureInfo.InvariantCulture);
        var expandable = new JsonObject();
        async Task Field(string name, string path, Func<Expand, Task<JsonNode?>> value)
        {
            if (expand.Has(name)) o[name] = await value(expand.Inside(name));
            else expandable[name] = path;
        }
        await Field("space", spacePath, e => Task.FromResult<JsonNode?>(DcSpaceJson.Of(space, Links, e, Icon)));
        await Field("history", self + "/history", async e => await HistoryAsync(actor, p, versions, shown, e, ct));
        await Field("version", "", async e => await VersionJsonAsync(actor, p, shown, e, ct));
        await Field("ancestors", "", async _ => await AncestorsAsync(p, ct));
        await Field("container", spacePath, e => Task.FromResult<JsonNode?>(DcSpaceJson.Of(space, Links, e, Icon)));
        await Field("body", "", async e => await BodyAsync(actor, p, historical ? shown.ContentJson : p.ContentJson, historical ? shown.Title : p.Title, e, ct));
        await Field("children", self + "/child", async e => await ByTypeAsync(actor, p, e, descendants: false, new Paging(0, 25), ct));
        await Field("descendants", self + "/descendant", async e => await ByTypeAsync(actor, p, e, descendants: true, new Paging(0, 25), ct));
        await Field("metadata", "", async e => await MetadataAsync(p, ct));
        await Field("restrictions", self + "/restriction/byOperation", _ => Task.FromResult<JsonNode?>(Restrictions()));
        await Field("operations", "", async _ => await OperationsAsync(actor, p, canEdit, ct));
        var links = new JsonObject { ["self"] = Self(id), ["webui"] = "/p/" + p.Id, ["tinyui"] = "/p/" + p.Id };
        if (canEdit) links["edit"] = "/p/" + p.Id;
        if (top) links["collection"] = "/rest/api/content";
        o["_links"] = links;
        o["_expandable"] = expandable;
        return top ? Links.Top(o) : o;
    }

    private async Task<JsonObject> VersionJsonAsync(Actor actor, Page p, DcPageVersion v, Expand expand, CancellationToken ct)
    {
        var id = await IdOfAsync(p.Id, ct);
        var o = new JsonObject
        {
            ["by"] = await UserJsonAsync(v.AuthorId, expand.Inside("by"), ct),
            ["when"] = DcSpaceJson.Date(v.At),
            ["message"] = v.Note ?? "",
            ["number"] = v.Number,
            ["minorEdit"] = v.Minor != 0,
            ["hidden"] = false,
            ["_links"] = new JsonObject { ["self"] = Self(id) + "/version/" + v.Number.ToString(CultureInfo.InvariantCulture) },
        };
        if (expand.Has("content")) o["content"] = await ContentAsync(actor, p, Expand.None, ct, v.Number);
        else o["_expandable"] = new JsonObject { ["content"] = "/rest/api/content/" + id.ToString(CultureInfo.InvariantCulture) + "?version=" + v.Number.ToString(CultureInfo.InvariantCulture) };
        return o;
    }

    /// <summary>Whether the caller may see the page's history: a member of its space may (<see cref="PageScopes.SeesHistoryAsync"/>).</summary>
    public Task<bool> SeesHistoryAsync(Actor actor, Page p, CancellationToken ct) => access.SeesHistoryAsync(actor, p.SpaceId, ct);

    public async Task<JsonObject> HistoryAsync(Actor actor, Page p, IReadOnlyList<DcPageVersion> versions, DcPageVersion shown, Expand expand, CancellationToken ct)
    {
        var current = Current(p, versions);
        var first = versions.Count > 0 ? versions[0] : null;
        var o = new JsonObject
        {
            ["latest"] = shown.Number == current.Number,
            ["createdBy"] = await UserJsonAsync(first?.AuthorId ?? p.CreatedBy, expand.Inside("createdBy"), ct),
            ["createdDate"] = DcSpaceJson.Date(first?.At ?? p.CreatedAt),
        };
        var expandable = new JsonObject();
        var prev = versions.LastOrDefault(v => v.Number < shown.Number);
        var next = versions.FirstOrDefault(v => v.Number > shown.Number);
        async Task Field(string name, Func<Task<JsonNode?>> value)
        {
            if (expand.Has(name)) o[name] = await value();
            else expandable[name] = "";
        }
        await Field("lastUpdated", async () => await VersionJsonAsync(actor, p, current, expand.Inside("lastUpdated"), ct));
        if (prev is not null) await Field("previousVersion", async () => await VersionJsonAsync(actor, p, prev, expand.Inside("previousVersion"), ct));
        if (next is not null) await Field("nextVersion", async () => await VersionJsonAsync(actor, p, next, expand.Inside("nextVersion"), ct));
        await Field("contributors", async () =>
        {
            var authors = versions.Select(v => v.AuthorId).Distinct().ToList();
            var users = new JsonArray();
            foreach (var a in authors) users.Add(await UserJsonAsync(a, Expand.None, ct));
            return new JsonObject { ["publishers"] = new JsonObject { ["users"] = users, ["userKeys"] = new JsonArray([.. authors.Select(a => (JsonNode)a.ToString("N"))]) } };
        });
        o["_links"] = new JsonObject { ["self"] = Self(await IdOfAsync(p.Id, ct)) + "/history" };
        o["_expandable"] = expandable;
        return o;
    }

    /// <summary>The folders above, top first.</summary>
    private async Task<JsonArray> AncestorsAsync(Page p, CancellationToken ct)
    {
        var chain = new List<Page>();
        var parent = p.ParentId;
        for (var i = 0; parent is { } pid && i < 50; i++)
        {
            var row = await db.Pages.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.Id == pid, ct); // system: the folders above a page the caller sees, in its own space
            if (row is null) break;
            chain.Insert(0, row);
            parent = row.ParentId;
        }
        var list = new JsonArray();
        foreach (var a in chain) list.Add(await ShortAsync(a, ct));
        return list;
    }

    private static readonly string[] Representations = ["storage", "editor", "view", "export_view", "styled_view", "anonymous_export_view"];

    public async Task<JsonObject> BodyAsync(Actor actor, Page p, string contentJson, string title, Expand expand, CancellationToken ct)
    {
        var storage = StorageFormat.FromDoc(contentJson);
        var o = new JsonObject();
        var expandable = new JsonObject();
        foreach (var r in Representations)
        {
            if (!expand.Has(r)) { expandable[r] = ""; continue; }
            o[r] = new JsonObject { ["value"] = await RenderAsync(actor, p, storage, title, r, ct), ["representation"] = r, ["_expandable"] = new JsonObject { ["content"] = "" } };
        }
        o["_expandable"] = expandable;
        return o;
    }

    /// <summary>A storage body in one representation. editor is aerie's editor markup, which is the storage markup aerie writes.</summary>
    public async Task<string> RenderAsync(Actor actor, Page? context, string storage, string title, string representation, CancellationToken ct)
    {
        if (representation is "storage" or "editor") return storage;
        var links = new StorageLinks(this, actor, context);
        var export = representation is "export_view" or "styled_view" or "anonymous_export_view";
        var html = await StorageView.RenderAsync(storage, links, export ? Links.Base : "");
        return representation == "styled_view" ? StorageView.Styled(html, title) : html;
    }

    private async Task<JsonObject> MetadataAsync(Page p, CancellationToken ct)
    {
        var tags = await db.PageTags.AsNoTracking().Where(t => t.PageId == p.Id).OrderBy(t => t.Tag).Select(t => t.Tag).ToListAsync(ct);
        var labels = new JsonArray();
        foreach (var t in tags) labels.Add(await LabelAsync("global", t, ct));
        return new JsonObject
        {
            ["labels"] = new JsonObject { ["results"] = labels, ["start"] = 0, ["limit"] = 200, ["size"] = labels.Count, ["_links"] = new JsonObject { ["self"] = Self(await IdOfAsync(p.Id, ct)) + "/label" } },
        };
    }

    public async Task<JsonObject> LabelAsync(string prefix, string name, CancellationToken ct)
    {
        var id = await db.DcLabels.Where(l => l.Prefix == prefix && l.Name == name).Select(l => (long?)l.Id).FirstOrDefaultAsync(ct);
        if (id is null)
        {
            var row = new DcLabel { Prefix = prefix, Name = name };
            db.DcLabels.Add(row);
            try { await db.SaveChangesAsync(ct); id = row.Id; }
            catch (DbUpdateException)
            {
                db.Entry(row).State = EntityState.Detached;
                id = await db.DcLabels.Where(l => l.Prefix == prefix && l.Name == name).Select(l => l.Id).FirstAsync(ct);
            }
        }
        return new JsonObject { ["prefix"] = prefix, ["name"] = name, ["id"] = id.Value.ToString(CultureInfo.InvariantCulture), ["label"] = prefix == "global" ? name : prefix + ":" + name };
    }

    /// <summary>T1 has no page restrictions: both lists are empty (content.md).</summary>
    private static JsonObject Restrictions()
    {
        JsonObject Empty() => new() { ["results"] = new JsonArray(), ["start"] = 0, ["limit"] = 200, ["size"] = 0 };
        JsonObject One(string op) => new() { ["operation"] = op, ["restrictions"] = new JsonObject { ["user"] = Empty(), ["group"] = Empty() } };
        return new JsonObject { ["read"] = One("read"), ["update"] = One("update") };
    }

    private async Task<JsonArray> OperationsAsync(Actor actor, Page p, bool canEdit, CancellationToken ct)
    {
        var ops = new JsonArray(new JsonObject { ["operation"] = "read", ["targetType"] = "page" });
        if (canEdit) { ops.Add(new JsonObject { ["operation"] = "update", ["targetType"] = "page" }); ops.Add(new JsonObject { ["operation"] = "create", ["targetType"] = "page" }); }
        if (await PageSupport.CanRemoveAsync(access, actor, p.SpaceId, [p])) ops.Add(new JsonObject { ["operation"] = "delete", ["targetType"] = "page" });
        return ops;
    }

    // ---- children and descendants ----

    /// <summary>The current pages directly in a folder, in aerie's sibling order; a page has none.</summary>
    public async Task<List<Page>> ChildrenAsync(Page p, CancellationToken ct) =>
        p.Kind != PageKind.Folder ? [] : await db.Pages.AsNoTracking().Where(x => x.ParentId == p.Id && x.SpaceId == p.SpaceId).OrderBy(x => x.Sort).ThenBy(x => x.Title).ToListAsync(ct); // system: the children of a page the caller sees, in its space

    /// <summary>Every current page below a folder, depth-first in sibling order.</summary>
    public async Task<List<Page>> DescendantsAsync(Page p, CancellationToken ct)
    {
        if (p.Kind != PageKind.Folder) return [];
        var all = await db.Pages.AsNoTracking().Where(x => x.SpaceId == p.SpaceId).ToListAsync(ct); // system: the tree of a space the caller sees
        var kids = all.Where(x => x.ParentId is not null).ToLookup(x => x.ParentId!.Value);
        var list = new List<Page>();
        void Walk(Guid id) { foreach (var k in kids[id].OrderBy(x => x.Sort).ThenBy(x => x.Title)) { list.Add(k); Walk(k.Id); } }
        Walk(p.Id);
        return list;
    }

    /// <summary>A ContentByType map: only the types asked for with expand are filled.</summary>
    public async Task<JsonObject> ByTypeAsync(Actor actor, Page p, Expand expand, bool descendants, Paging paging, CancellationToken ct)
    {
        var id = await IdOfAsync(p.Id, ct);
        var path = "/rest/api/content/" + id.ToString(CultureInfo.InvariantCulture) + (descendants ? "/descendant" : "/child");
        var o = new JsonObject();
        var expandable = new JsonObject();
        foreach (var type in new[] { "page", "comment", "attachment" })
        {
            if (!expand.Has(type)) { expandable[type] = path + "/" + type; continue; }
            if (type == "attachment")
            {
                // Attachments on the content itself, for children and descendants alike (content.md).
                var files = http.HttpContext!.RequestServices.GetRequiredService<DcAttachmentService>();
                o[type] = await files.PageOfAsync(actor, p, await files.OfPageAsync(p.Id, ct), paging, expand.Inside(type), path + "/" + type, ct);
                continue;
            }
            var items = type == "page" ? (descendants ? await DescendantsAsync(p, ct) : await ChildrenAsync(p, ct)) : [];
            o[type] = await PageOfAsync(actor, items, paging, expand.Inside(type), path + "/" + type, ct);
        }
        o["_links"] = new JsonObject { ["self"] = Links.Abs(path) };
        o["_expandable"] = expandable;
        return o;
    }

    /// <summary>One page of a list inside another object: results, start, limit, size and links of its own.</summary>
    public async Task<JsonObject> PageOfAsync(Actor actor, IReadOnlyList<Page> all, Paging paging, Expand expand, string restPath, CancellationToken ct)
    {
        var results = new JsonArray();
        foreach (var x in all.Skip(paging.Start).Take(paging.Limit)) results.Add(await ContentAsync(actor, x, expand, ct));
        var links = new JsonObject { ["self"] = Links.Abs(restPath) };
        if (all.Count > paging.Start + paging.Limit) links["next"] = $"{restPath}?limit={paging.Limit}&start={paging.Start + paging.Limit}";
        if (paging.Start > 0) links["prev"] = $"{restPath}?limit={paging.Limit}&start={Math.Max(0, paging.Start - paging.Limit)}";
        return new JsonObject { ["results"] = results, ["start"] = paging.Start, ["limit"] = paging.Limit, ["size"] = results.Count, ["_links"] = links };
    }

    // ---- links in bodies ----

    private sealed class StorageLinks(DcContentService svc, Actor actor, Page? context) : IStorageLinks
    {
        public async Task<string?> PageUrlAsync(string? spaceKey, string title)
        {
            Guid? spaceId = context?.SpaceId;
            if (spaceKey is not null) spaceId = (await DcSpaceData.FindAsync(svc.Db, svc.Access, actor, spaceKey, CancellationToken.None))?.Space.Id;
            if (spaceId is not { } sid) return null;
            var like = Like(title);
            var page = await svc.Db.Pages.AsNoTracking().Where(p => p.SpaceId == sid && EF.Functions.Like(p.Title, like, "\\")).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(); // system: a space the caller has a role in
            return page is { } id ? "/p/" + id : null;
        }

        public async Task<string?> ContentUrlAsync(string id) =>
            await svc.FindAsync(actor, id, ["current"], track: false, CancellationToken.None) is { } p ? "/p/" + p.Id : null;

        public async Task<string?> SpaceUrlAsync(string spaceKey) =>
            await DcSpaceData.FindAsync(svc.Db, svc.Access, actor, spaceKey, CancellationToken.None) is { } s ? "/s/" + s.Space.Id : null;

        public async Task<string?> UserNameAsync(string? userKey, string? userName)
        {
            KbUser? u = null;
            if (userKey is not null && Guid.TryParseExact(userKey, "N", out var id)) u = await svc.Db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            else if (userName is not null) { var n = userName.ToUpperInvariant(); u = await svc.Db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.NormalizedUserName == n); }
            return u is null || u.DeletedAt is not null ? null : u.DisplayName;
        }

        public async Task<IReadOnlyList<(string Title, string Url)>> ChildrenAsync() =>
            context is null ? [] : [.. (await svc.ChildrenAsync(context, CancellationToken.None)).Select(c => (c.Title, "/p/" + c.Id))];
    }
}
