using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Kb.Core.Content;
using Kb.Data;
using Kb.Web.DataCenter;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

// CQL search on the Data Center compatible surface (contracts/behaviour/search-cql.md). The query is parsed, then run over the pages, folders
// and attachments in the caller's visible spaces, so a match the caller cannot see never counts.

[Action("dc.search", Read = true)]
public sealed record DcSearch : IAction<JsonObject>
{
    public required string Cql { get; init; }
    public string? CqlContext { get; init; }
    /// <summary>True for GET /rest/api/search (search results); false for GET /rest/api/content/search (content).</summary>
    public required bool Results { get; init; }
    public bool IncludeArchivedSpaces { get; init; }
    public string Excerpt { get; init; } = "highlight";
    public required int Start { get; init; }
    public required int Limit { get; init; }
    public required string Expand { get; init; }
}

internal sealed class DcSearchHandler(KbDbContext db, DcContentService content, DcAttachmentService files, TimeProvider time) : IActionHandler<DcSearch, JsonObject>
{
    private CqlQuery? _query;
    private CqlContext? _context;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DcSearch> ctx)
    {
        try
        {
            _query = Cql.Parse(ctx.Action.Cql);
            var (spaceKey, _) = Cql.Context(ctx.Action.CqlContext);
            var me = ctx.Actor.UserId is { } id ? await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ctx.Cancel) : null;
            _context = new CqlContext(me?.Id.ToString("N"), me?.UserName, spaceKey, time.GetUtcNow());
            return null;
        }
        catch (CqlException e) { return new Problem(400, e.Message); }
    }

    public async Task<Outcome<JsonObject>> HandleAsync(ActionContext<DcSearch> ctx)
    {
        var watch = Stopwatch.StartNew();
        var req = ctx.Action;
        var (items, archivedSpaces) = await ItemsAsync(ctx.Actor, ctx.Cancel);
        var matched = new List<(CqlItem Item, int Score)>();
        var archived = 0;
        try
        {
            foreach (var item in items)
            {
                var score = 0;
                if (!Cql.Matches(_query!.Where, item, _context!, ref score)) continue;
                // Archived spaces are left out of search unless asked for; only /search can ask.
                if (archivedSpaces.Contains(item.SpaceId) && !(req.Results && req.IncludeArchivedSpaces)) { archived++; continue; }
                matched.Add((item, score));
            }
        }
        catch (CqlException e) { return new Problem(400, e.Message); }

        IOrderedEnumerable<(CqlItem Item, int Score)> ordered;
        if (_query!.Order.Count == 0) ordered = matched.OrderByDescending(m => m.Score).ThenBy(m => m.Item.DcId); // relevance, then id
        else
        {
            ordered = Order(matched.OrderBy(_ => 0), _query.Order[0]);
            foreach (var by in _query.Order.Skip(1)) ordered = Order(ordered, by);
        }
        var page = ordered.Skip(req.Start).Take(req.Limit).ToList();
        var more = matched.Count > req.Start + req.Limit;
        var expand = Expand.Parse(req.Expand);
        var results = new List<JsonObject>();
        foreach (var (item, _) in page)
            results.Add(req.Results ? await ResultAsync(ctx.Actor, item, expand.Inside("content"), req.Excerpt, ctx.Cancel) : await ContentOfAsync(ctx.Actor, item, expand, ctx.Cancel));

        var o = content.Links.Page(new Paging(req.Start, req.Limit), results, more);
        if (req.Results)
        {
            o["totalSize"] = matched.Count;
            o["cqlQuery"] = req.Cql;
            o["searchDuration"] = (int)watch.ElapsedMilliseconds;
            o["archivedResultCount"] = archived;
        }
        return o;
    }

    private static IOrderedEnumerable<(CqlItem Item, int Score)> Order(IOrderedEnumerable<(CqlItem Item, int Score)> rows, (string Field, bool Descending) by)
    {
        Func<(CqlItem Item, int Score), IComparable> key = by.Field switch
        {
            "created" => m => m.Item.Created, "lastmodified" => m => m.Item.Modified, "title" => m => m.Item.Title.ToLowerInvariant(),
            "type" => m => m.Item.Type, "space" => m => m.Item.SpaceKey, _ => m => m.Item.DcId,
        };
        return by.Descending ? rows.ThenByDescending(key) : rows.ThenBy(key);
    }

    /// <summary>Every page, folder and attachment in the caller's visible spaces, current only, with the facts CQL reads.</summary>
    private async Task<(List<CqlItem> Items, HashSet<Guid> Archived)> ItemsAsync(Actor actor, CancellationToken ct)
    {
        // shortcut: the query runs in memory over every visible item; translate it to SQL if spaces grow past a few thousand pages.
        var scope = await content.ScopeAsync(actor, ct);
        await content.EnsureIdsAsync(ct);
        await DcSpaceData.EnsureIdsAsync(db, ct);
        var pages = await db.Pages.AsNoTracking().In(scope).ToListAsync(ct);
        var pageIds = pages.Select(p => p.Id).ToList();
        var files = await db.Attachments.AsNoTracking().Where(a => pageIds.Contains(a.PageId) && a.DeletedAt == null).ToListAsync(ct);
        var ids = await content.IdsOfAsync([.. pageIds, .. files.Select(f => f.Id)], ct);
        var spaces = await db.Spaces.AsNoTracking().Where(s => scope.SpaceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var dcSpaces = await db.DcSpaces.AsNoTracking().Where(d => scope.SpaceIds.Contains(d.SpaceId)).ToDictionaryAsync(d => d.SpaceId, ct);
        var tags = (await db.PageTags.AsNoTracking().Where(t => pageIds.Contains(t.PageId)).ToListAsync(ct)).ToLookup(t => t.PageId, t => t.Tag);
        var authors = (await db.PageVersions.AsNoTracking().Where(v => pageIds.Contains(v.PageId)).Select(v => new { v.PageId, v.AuthorId }).Distinct().ToListAsync(ct)).ToLookup(v => v.PageId, v => v.AuthorId);
        var userIds = pages.Select(p => p.CreatedBy).Concat(files.Select(f => f.CreatedBy)).Concat(authors.SelectMany(a => a)).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.UserName ?? "", ct);
        var byId = pages.ToDictionary(p => p.Id);
        (string, string) Person(Guid id) => (id.ToString("N"), users.GetValueOrDefault(id, ""));
        List<long> Above(Page p)
        {
            var chain = new List<long>();
            var parent = p.ParentId;
            for (var i = 0; parent is { } pid && i < 50 && byId.TryGetValue(pid, out var up); i++) { chain.Add(ids[up.Id]); parent = up.ParentId; }
            return chain;
        }
        string Key(Guid space) => dcSpaces.TryGetValue(space, out var d) && d.PersonalOwnerId is not null ? spaces[space].Key : spaces[space].Key.ToUpperInvariant();

        var items = new List<CqlItem>();
        foreach (var p in pages)
        {
            var space = spaces[p.SpaceId];
            var labels = tags[p.Id].ToList();
            var contributors = authors[p.Id].Select(Person).ToList();
            if (contributors.Count == 0) contributors.Add(Person(p.CreatedBy));
            items.Add(new CqlItem(p.Id, ids[p.Id], "page", p.SpaceId, Key(p.SpaceId), space.Name, dcSpaces.GetValueOrDefault(p.SpaceId)?.PersonalOwnerId is not null, p.Title,
                p.SearchText, labels, Person(p.CreatedBy).Item1, Person(p.CreatedBy).Item2, contributors, p.CreatedAt, p.UpdatedAt,
                p.ParentId is { } parent ? ids[parent] : null, Above(p), null));
        }
        foreach (var f in files)
        {
            var page = byId[f.PageId];
            items.Add(new CqlItem(f.Id, ids[f.Id], "attachment", page.SpaceId, Key(page.SpaceId), spaces[page.SpaceId].Name, dcSpaces.GetValueOrDefault(page.SpaceId)?.PersonalOwnerId is not null,
                f.FileName, "", [], Person(f.CreatedBy).Item1, Person(f.CreatedBy).Item2, [Person(f.CreatedBy)], f.CreatedAt, f.CreatedAt, null, [ids[page.Id], .. Above(page)], ids[page.Id]));
        }
        var archived = dcSpaces.Values.Where(d => d.ArchivedAt is not null).Select(d => d.SpaceId).ToHashSet();
        return (items, archived);
    }

    private async Task<JsonObject> ContentOfAsync(Actor actor, CqlItem item, Expand expand, CancellationToken ct)
    {
        if (item.Type == "attachment")
        {
            var a = await db.Attachments.AsNoTracking().FirstAsync(x => x.Id == item.Id, ct);
            var page = await db.Pages.AsNoTracking().FirstAsync(p => p.Id == a.PageId, ct); // system: the page of an attachment the caller sees, found above in their spaces
            return await files.JsonAsync(actor, a, page, expand, ct);
        }
        return await content.ContentAsync(actor, await db.Pages.AsNoTracking().FirstAsync(p => p.Id == item.Id, ct), expand, ct); // system: a page found above in the caller's spaces
    }

    private const string Open = "@@@hl@@@", Close = "@@@endhl@@@";

    /// <summary>One search result: the content, its title and excerpt (with highlight marks when asked), where it is, and when it changed.</summary>
    private async Task<JsonObject> ResultAsync(Actor actor, CqlItem item, Expand expand, string excerpt, CancellationToken ct)
    {
        var words = Cql.TextWords(_query!.Where);
        var url = "/p/" + (item.Type == "attachment" ? (await db.Attachments.AsNoTracking().Where(a => a.Id == item.Id).Select(a => a.PageId).FirstAsync(ct)) : item.Id);
        var parent = item.Parent is { } p ? await db.DcContents.Where(d => d.Id == p).Select(d => (Guid?)d.AerieId).FirstOrDefaultAsync(ct) : null;
        var parentTitle = parent is { } pid ? await db.Pages.AsNoTracking().Where(x => x.Id == pid).Select(x => x.Title).FirstOrDefaultAsync(ct) : null; // system: the folder above a page the caller sees
        var o = new JsonObject
        {
            ["content"] = await ContentOfAsync(actor, item, expand, ct),
            ["title"] = excerpt == "highlight" ? Mark(item.Title, words) : item.Title,
            ["excerpt"] = excerpt switch { "none" => "", "indexed" => Head(item.Text), _ => Snippet(item.Text, words) },
            ["url"] = url,
            ["resultGlobalContainer"] = new JsonObject { ["title"] = item.SpaceName, ["displayUrl"] = "/s/" + item.SpaceId },
            ["entityType"] = "content",
            ["iconCssClass"] = "aerie-icon-" + item.Type,
            ["lastModified"] = DcSpaceJson.Date(item.Modified),
            ["friendlyLastModified"] = item.Modified.UtcDateTime.ToString("MMM dd, yyyy", CultureInfo.InvariantCulture),
        };
        if (parent is { } folder && parentTitle is not null) o["resultParentContainer"] = new JsonObject { ["title"] = parentTitle, ["displayUrl"] = "/p/" + folder };
        return o;
    }

    private static string Head(string text) => text.Length <= 300 ? text : text[..300];

    private static string Snippet(string text, IReadOnlyList<string> words)
    {
        var at = words.Select(w => text.IndexOf(w, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, at - 100);
        return Mark(text.Substring(start, Math.Min(300, text.Length - start)), words);
    }

    /// <summary>Each query word in the text between @@@hl@@@ and @@@endhl@@@ (the marks are aerie's choice, search-cql.md).</summary>
    private static string Mark(string text, IReadOnlyList<string> words)
    {
        foreach (var w in words.Where(w => w.Length > 1).Distinct())
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\b(" + System.Text.RegularExpressions.Regex.Escape(w) + @"\w*)", Open + "$1" + Close,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        return text;
    }
}
