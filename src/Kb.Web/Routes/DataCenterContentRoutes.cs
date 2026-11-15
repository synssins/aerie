using System.Globalization;
using System.Text.Json.Nodes;
using Kb.Web.Actions;
using Microsoft.AspNetCore.WebUtilities;

namespace Kb.Web.DataCenter;

/// <summary>Content, children, versions, a space's content and trash, and body conversion (contracts/behaviour/content.md, spaces.md, representations.md).</summary>
public static class DcContentRoutes
{
    private const string Single = "space,history,version";
    private static readonly string[] ChildTypes = ["page", "blogpost", "comment", "attachment"];

    public static void MapDcContent(this RouteGroupBuilder dc)
    {
        static IReadOnlyList<string>? Statuses(HttpRequest q) => DcContentRules.Statuses(DcRest.Values(q, "status"));
        static IResult BadStatus() => DcRest.Fail(400, "status is current, trashed, draft or any.");

        dc.MapGet("/content", (HttpContext http, IActionBus bus, CancellationToken ct) =>
        {
            var (links, q) = (DcLinks.Of(http), http.Request);
            if (Paging.Read(q, 25, links.MaxLimit) is not { } paging) return Task.FromResult(Paging.Invalid);
            var type = q.Query["type"].ToString() is { Length: > 0 } t ? t : "page";
            if (type is not ("page" or "blogpost")) return Task.FromResult(DcRest.Fail(400, "type is page or blogpost."));
            if (Statuses(q) is not { } statuses) return Task.FromResult(BadStatus());
            if (q.Query["postingDay"].ToString() is { Length: > 0 } day && !DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return Task.FromResult(DcRest.Fail(400, "postingDay is a date, yyyy-mm-dd."));
            var ids = DcRest.Values(q, "ids");
            return bus.SendAsync(new DcListContent
            {
                Type = type, SpaceKey = q.Query["spaceKey"].ToString() is { Length: > 0 } k ? k : null, Title = q.Query["title"].ToString() is { Length: > 0 } title ? title : null,
                Statuses = statuses, Ids = ids.Count > 0 ? ids : null, Start = paging.Start, Limit = paging.Limit, Expand = Expand.Raw(q),
            }, ct).Rest(page => DcRest.Json(links.Page(paging, page.Items, page.More)));
        }).WithAction<DcListContent>();

        dc.MapPost("/content", async (HttpContext http, IActionBus bus, CancellationToken ct) =>
        {
            var status = http.Request.Query["status"].ToString();
            if (status.Length > 0 && status is not ("current" or "draft")) return DcRest.Fail(400, "status is current or draft.");
            if (await DcRest.BodyAsync(http.Request) is not { } body) return DcRest.BadBody;
            return await bus.SendAsync(new DcCreateContent { Body = body, Status = status.Length > 0 ? status : null, Expand = Expand.Raw(http.Request, Single) }, ct).Rest(o => DcRest.Json(o));
        }).WithAction<DcCreateContent>();

        dc.MapGet("/content/scan", (HttpContext http, IActionBus bus, CancellationToken ct) =>
        {
            var (links, q) = (DcLinks.Of(http), http.Request);
            if (Paging.Read(q, 25, links.MaxLimit) is not { } paging) return Task.FromResult(Paging.Invalid);
            var type = q.Query["type"].ToString() is { Length: > 0 } t ? t : "page";
            if (type is not ("page" or "blogpost" or "comment")) return Task.FromResult(DcRest.Fail(400, "type is page, blogpost or comment."));
            if (Statuses(q) is not { } statuses) return Task.FromResult(BadStatus());
            return bus.SendAsync(new DcScanContent
            {
                Cursor = q.Query["cursor"].ToString() is { Length: > 0 } c ? c : null, Type = type, SpaceKey = q.Query["spaceKey"].ToString() is { Length: > 0 } k ? k : null,
                Statuses = statuses, Limit = paging.Limit, Expand = Expand.Raw(q),
            }, ct).Rest(page => DcRest.Json(CursorPage(http, page, paging.Limit)));
        }).WithAction<DcScanContent>();

        dc.MapGet("/content/{id}", (HttpContext http, string id, IActionBus bus, CancellationToken ct) =>
        {
            var q = http.Request;
            if (Statuses(q) is not { } statuses) return Task.FromResult(BadStatus());
            int? version = null;
            if (q.Query["version"].ToString() is { Length: > 0 } v)
            {
                if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1) return Task.FromResult(DcRest.Fail(404, "No such version."));
                version = n;
            }
            return bus.SendAsync(new DcReadContent { Id = id, Statuses = statuses, Version = version, Expand = Expand.Raw(q, Single) }, ct).Rest(o => DcRest.Json(o));
        }).WithAction<DcReadContent>();

        dc.MapDelete("/content/{id}", (HttpContext http, string id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new DcDeleteContent { Id = id, Status = http.Request.Query["status"].ToString() is { Length: > 0 } s ? s : "current" }, ct).Rest(_ => Results.NoContent()))
            .WithAction<DcDeleteContent>();

        dc.MapPut("/content/{id}", async (HttpContext http, string id, IActionBus bus, CancellationToken ct) =>
        {
            var q = http.Request.Query;
            if (q["conflictPolicy"].ToString() is { Length: > 0 } policy && policy != "abort") return DcRest.Fail(400, "conflictPolicy is abort, the only one aerie supports.");
            if (await DcRest.BodyAsync(http.Request) is not { } body) return DcRest.BadBody;
            // asyncReconciliation is accepted and ignored: aerie reconciles at once.
            return await bus.SendAsync(new DcUpdateContent { Id = id, Body = body, Status = q["status"].ToString() is { Length: > 0 } s ? s : "current", Expand = Expand.Raw(http.Request, Single) }, ct)
                .Rest(o => DcRest.Json(o));
        }).WithAction<DcUpdateContent>();

        dc.MapGet("/content/{id}/history", (HttpContext http, string id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new DcContentHistory { Id = id, Expand = Expand.Raw(http.Request) }, ct).Rest(o => DcRest.Json(o))).WithAction<DcContentHistory>();

        dc.MapGet("/content/{id}/history/{version}/macro/hash/{hash}", (string id, string version, string hash, IActionBus bus, CancellationToken ct) =>
            int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v >= 1
                ? bus.SendAsync(new DcReadMacro { Id = id, Version = v, Hash = hash.ToLowerInvariant() }, ct).Rest(o => DcRest.Json(o))
                : Task.FromResult(DcRest.Fail(404, "No such version."))).WithAction<DcReadMacro>();

        dc.MapGet("/content/{id}/history/{version}/macro/id/{macroId}", (string id, string version, string macroId, IActionBus bus, CancellationToken ct) =>
            int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v >= 1
                ? bus.SendAsync(new DcReadMacro { Id = id, Version = v, MacroId = macroId }, ct).Rest(o => DcRest.Json(o))
                : Task.FromResult(DcRest.Fail(404, "No such version."))).WithAction<DcReadMacro>();

        dc.MapDelete("/content/{id}/version/{versionNumber}", (string id, string versionNumber, IActionBus bus, CancellationToken ct) =>
            int.TryParse(versionNumber, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1
                ? bus.SendAsync(new DcRemoveVersion { Id = id, Number = n }, ct).Rest(_ => Results.NoContent())
                : Task.FromResult(DcRest.Fail(400, "A version number is 1 or more."))).WithAction<DcRemoveVersion>();

        Task<IResult> Children(HttpContext http, string id, string? type, bool descendants, IActionBus bus, CancellationToken ct)
        {
            var (links, q) = (DcLinks.Of(http), http.Request);
            if (Paging.Read(q, 25, links.MaxLimit) is not { } paging) return Task.FromResult(Paging.Invalid);
            if (type is not null && !ChildTypes.Contains(type)) return Task.FromResult(DcRest.Fail(400, "type is page, blogpost, comment or attachment."));
            // Children as they were at an older version of the parent: aerie supports 0, now, only.
            if (q.Query["parentVersion"].ToString() is { Length: > 0 } pv && pv != "0") return Task.FromResult(DcRest.Fail(400, "parentVersion 0 is the only one aerie supports."));
            return bus.SendAsync(new DcContentChildren { Id = id, Descendants = descendants, Type = type, Start = paging.Start, Limit = paging.Limit, Expand = Expand.Raw(q) }, ct)
                .Rest(o => DcRest.Json(type is null ? o : links.Repage(o, paging)));
        }
        dc.MapGet("/content/{id}/child", (HttpContext http, string id, IActionBus bus, CancellationToken ct) => Children(http, id, null, false, bus, ct)).WithAction<DcContentChildren>();
        dc.MapGet("/content/{id}/child/{type}", (HttpContext http, string id, string type, IActionBus bus, CancellationToken ct) => Children(http, id, type, false, bus, ct)).WithAction<DcContentChildren>();
        dc.MapGet("/content/{id}/child/comment", (HttpContext http, string id, IActionBus bus, CancellationToken ct) =>
        {
            var q = http.Request.Query;
            if (q["depth"].ToString() is { Length: > 0 } depth && depth != "all") return Task.FromResult(DcRest.Fail(400, "depth is empty or all."));
            if (DcRest.Values(http.Request, "location").Any(l => l is not ("inline" or "footer" or "resolved"))) return Task.FromResult(DcRest.Fail(400, "location is inline, footer or resolved."));
            return Children(http, id, "comment", false, bus, ct);
        }).WithAction<DcContentChildren>();
        dc.MapGet("/content/{id}/descendant", (HttpContext http, string id, IActionBus bus, CancellationToken ct) => Children(http, id, null, true, bus, ct)).WithAction<DcContentChildren>();
        dc.MapGet("/content/{id}/descendant/{type}", (HttpContext http, string id, string type, IActionBus bus, CancellationToken ct) => Children(http, id, type, true, bus, ct)).WithAction<DcContentChildren>();
        dc.MapGet("/content/{id}/descendant/page/count", (string id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new DcCountDescendants { Id = id }, ct).Rest(n => DcRest.Json(new JsonObject { ["count"] = n }))).WithAction<DcCountDescendants>();

        Task<IResult> SpaceContent(HttpContext http, string spaceKey, string? type, IActionBus bus, CancellationToken ct)
        {
            var (links, q) = (DcLinks.Of(http), http.Request);
            if (Paging.Read(q, 25, links.MaxLimit) is not { } paging) return Task.FromResult(Paging.Invalid);
            if (type is not null && type is not ("page" or "blogpost")) return Task.FromResult(DcRest.Fail(400, "type is page or blogpost."));
            var depth = q.Query["depth"].ToString() is { Length: > 0 } d ? d : "all";
            if (depth is not ("all" or "root")) return Task.FromResult(DcRest.Fail(400, "depth is all or root."));
            return bus.SendAsync(new DcSpaceContent { Key = spaceKey, Type = type, RootOnly = depth == "root", Start = paging.Start, Limit = paging.Limit, Expand = Expand.Raw(q) }, ct)
                .Rest(o => DcRest.Json(type is null ? o : links.Repage(o, paging)));
        }
        dc.MapGet("/space/{spaceKey}/content", (HttpContext http, string spaceKey, IActionBus bus, CancellationToken ct) => SpaceContent(http, spaceKey, null, bus, ct)).WithAction<DcSpaceContent>();
        dc.MapGet("/space/{spaceKey}/content/{type}", (HttpContext http, string spaceKey, string type, IActionBus bus, CancellationToken ct) => SpaceContent(http, spaceKey, type, bus, ct)).WithAction<DcSpaceContent>();

        dc.MapGet("/space/{spaceKey}/trash", (HttpContext http, string spaceKey, IActionBus bus, CancellationToken ct) =>
        {
            var (links, q) = (DcLinks.Of(http), http.Request);
            if (Paging.Read(q, 25, links.MaxLimit) is not { } paging) return Task.FromResult(Paging.Invalid);
            return bus.SendAsync(new DcListTrash { Key = spaceKey, Cursor = q.Query["cursor"].ToString() is { Length: > 0 } c ? c : null, Limit = paging.Limit, Expand = Expand.Raw(q) }, ct)
                .Rest(page => DcRest.Json(CursorPage(http, page, paging.Limit)));
        }).WithAction<DcListTrash>();

        dc.MapDelete("/space/{spaceKey}/trash", (string spaceKey, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new DcEmptyTrash { Key = spaceKey }, ct).Rest(_ => Results.NoContent())).WithAction<DcEmptyTrash>();

        dc.MapPost("/contentbody/convert/{to}", async (HttpContext http, string to, IActionBus bus, CancellationToken ct) =>
        {
            if (to is not ("storage" or "view" or "export_view" or "styled_view" or "editor")) return DcRest.Fail(400, "to is storage, view, export_view, styled_view or editor.");
            if (await DcRest.BodyAsync(http.Request) is not { } body) return DcRest.BadBody;
            var id = body["content"]?["id"] is JsonValue v ? v.ToString() : null;
            return await bus.SendAsync(new DcConvertBody { To = to, Value = DcRest.Text(body, "value"), Representation = DcRest.Text(body, "representation"), ContentId = id, Expand = Expand.Raw(http.Request) }, ct)
                .Rest(o => DcRest.Json(o));
        }).WithAction<DcConvertBody>();
    }

    /// <summary>A cursor page: the cursor applied, the ones either way where a page exists, and links built with them.</summary>
    private static JsonObject CursorPage(HttpContext http, DcCursorPage page, int limit)
    {
        var links = DcLinks.Of(http);
        string With(string cursor)
        {
            var query = http.Request.Query.Where(x => x.Key != "cursor").SelectMany(x => x.Value.Select(v => new KeyValuePair<string, string?>(x.Key, v))).ToList();
            query.Add(new("cursor", cursor));
            return QueryHelpers.AddQueryString(links.RequestPath, query);
        }
        var l = new JsonObject { ["self"] = links.Abs(links.RequestPath + http.Request.QueryString) };
        var o = new JsonObject { ["results"] = new JsonArray([.. page.Items]), ["limit"] = limit, ["size"] = page.Items.Count };
        if (page.Cursor is { } c) o["cursor"] = c;
        if (page.Next is { } n) { o["nextCursor"] = n; l["next"] = With(n); }
        if (page.Prev is { } p) { o["prevCursor"] = p; l["prev"] = With(p); }
        o["_links"] = l;
        return links.Top(o);
    }
}
