using System.Globalization;
using System.Text.Json.Nodes;
using Kb.Core.Config;
using Kb.Web.Actions;
using Microsoft.Extensions.Options;

namespace Kb.Web.DataCenter;

/// <summary>Spaces and long tasks of the Data Center compatible surface (contracts/behaviour/spaces.md, common.md).</summary>
public static class DcSpaceRoutes
{
    private static readonly string[] Types = ["global", "personal"];
    private static readonly string[] Statuses = ["current", "archived"];

    public static void MapDcSpaces(this RouteGroupBuilder dc)
    {
        string Icon(HttpContext http) => http.RequestServices.GetRequiredService<IOptions<KbOptions>>().Value.Brand.LogoLightPath;
        IResult One(HttpContext http, DcSpaceView s) => DcRest.Json(DcLinks.Of(http).Top(DcSpaceJson.Of(s, DcLinks.Of(http), Expand.Of(http.Request), Icon(http), alone: true)));

        dc.MapGet("/space", (HttpContext http, IActionBus bus, CancellationToken ct) =>
        {
            var (links, q) = (DcLinks.Of(http), http.Request);
            if (Paging.Read(q, 25, links.MaxLimit) is not { } paging) return Task.FromResult(Paging.Invalid);
            var keys = DcRest.Values(q, "spaceKey", "spaceKeys");
            if (q.Query["spaceKeySingle"].ToString() is { Length: > 0 } single) keys.Add(single.Trim());
            var idText = DcRest.Values(q, "spaceId", "spaceIds");
            if (keys.Count > 0 && idText.Count > 0) return Task.FromResult(DcRest.Fail(400, "Key and id filters cannot be combined."));
            var ids = new List<long>();
            foreach (var t in idText)
                if (long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) ids.Add(id);
                else return Task.FromResult(DcRest.Fail(400, "A space id is a whole number."));
            var type = q.Query["type"].ToString();
            if (type.Length > 0 && !Types.Contains(type)) return Task.FromResult(DcRest.Fail(400, "type is global or personal."));
            var status = q.Query["status"].ToString();
            if (status.Length > 0 && !Statuses.Contains(status)) return Task.FromResult(DcRest.Fail(400, "status is current or archived."));
            bool? Flag(string name) => q.Query[name].ToString() is { Length: > 0 } v ? bool.TryParse(v, out var b) ? b : null : false;
            if (Flag("favourite") is not { } favourite || Flag("hasRetentionPolicy") is not { } retention) return Task.FromResult(DcRest.Fail(400, "favourite and hasRetentionPolicy are true or false."));
            var expand = Expand.Of(q);
            return bus.SendAsync(new DcListSpaces
            {
                Keys = keys.Count > 0 ? keys : null, Ids = idText.Count > 0 ? ids : null, Type = type.Length > 0 ? type : null, Status = status.Length > 0 ? status : null,
                Labels = DcRest.Values(q, "label"), ContentLabels = DcRest.Values(q, "contentLabel"),
                // Aerie has no favourite spaces and no retention policies (spaces.md).
                NoneCanMatch = favourite || retention,
                Start = paging.Start, Limit = paging.Limit,
            }, ct).Rest(page => DcRest.Json(links.Page(paging, page.Spaces.Select(s => DcSpaceJson.Of(s, links, expand, Icon(http))), page.More)));
        }).WithAction<DcListSpaces>();

        async Task<IResult> CreateAsync(HttpContext http, IActionBus bus, bool isPrivate, CancellationToken ct)
        {
            if (await DcRest.BodyAsync(http.Request) is not { } body) return DcRest.BadBody;
            return await bus.SendAsync(new DcCreateSpace
            {
                Key = DcRest.Text(body, "key"), Name = DcRest.Text(body, "name"), Description = DcRest.Text(body, "description.plain.value"), Private = isPrivate,
            }, ct).Rest(s => One(http, s));
        }
        dc.MapPost("/space", (HttpContext http, IActionBus bus, CancellationToken ct) => CreateAsync(http, bus, false, ct)).WithAction<DcCreateSpace>();
        dc.MapPost("/space/_private", (HttpContext http, IActionBus bus, CancellationToken ct) => CreateAsync(http, bus, true, ct)).WithAction<DcCreateSpace>();

        dc.MapPost("/space/personal", async (HttpContext http, IActionBus bus, CancellationToken ct) =>
        {
            if (await DcRest.BodyAsync(http.Request) is not { } body) return DcRest.BadBody;
            // isPrivate wins over private when both are sent (spaces.md).
            var isPrivate = DcRest.Flag(body, "isPrivate") ?? DcRest.Flag(body, "private") ?? false;
            return await bus.SendAsync(new DcCreatePersonalSpace { Description = DcRest.Text(body, "description.plain.value"), Private = isPrivate }, ct).Rest(s => One(http, s));
        }).WithAction<DcCreatePersonalSpace>();

        dc.MapGet("/space/{spaceKey}", (HttpContext http, string spaceKey, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new DcReadSpace { Key = spaceKey }, ct).Rest(s => One(http, s))).WithAction<DcReadSpace>();

        dc.MapPut("/space/{spaceKey}", async (HttpContext http, string spaceKey, IActionBus bus, CancellationToken ct) =>
        {
            if (await DcRest.BodyAsync(http.Request) is not { } body) return DcRest.BadBody;
            return await bus.SendAsync(new DcUpdateSpace { Key = spaceKey, BodyKey = DcRest.Text(body, "key"), Name = DcRest.Text(body, "name"), Description = DcRest.Text(body, "description.plain.value") }, ct)
                .Rest(s => One(http, s));
        }).WithAction<DcUpdateSpace>();

        dc.MapDelete("/space/{spaceKey}", (HttpContext http, string spaceKey, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new DcDeleteSpace { Key = spaceKey }, ct).Rest(t => DcRest.Json(DcSpaceJson.Submission(t), 202))).WithAction<DcDeleteSpace>();

        dc.MapPut("/space/{spaceKey}/archive", (string spaceKey, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new DcArchiveSpace { Key = spaceKey, Archive = true }, ct).Rest(_ => Results.NoContent())).WithAction<DcArchiveSpace>();

        dc.MapPut("/space/{spaceKey}/restore", (string spaceKey, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new DcArchiveSpace { Key = spaceKey, Archive = false }, ct).Rest(_ => Results.NoContent())).WithAction<DcArchiveSpace>();

        dc.MapGet("/longtask", (HttpContext http, IActionBus bus, TimeProvider time, CancellationToken ct) =>
        {
            var links = DcLinks.Of(http);
            if (Paging.Read(http.Request, 100, links.MaxLimit) is not { } paging) return Task.FromResult(Paging.Invalid);
            var now = time.GetUtcNow();
            return bus.SendAsync(new DcListLongTasks { Start = paging.Start, Limit = paging.Limit }, ct)
                .Rest(page => DcRest.Json(links.Page(paging, page.Tasks.Select(t => (JsonNode)DcSpaceJson.Task(t, links, now)), page.More)));
        }).WithAction<DcListLongTasks>();

        dc.MapGet("/longtask/{id}", (HttpContext http, string id, IActionBus bus, TimeProvider time, CancellationToken ct) =>
            bus.SendAsync(new DcReadLongTask { Id = id }, ct).Rest(t => DcRest.Json(DcLinks.Of(http).Top(DcSpaceJson.Task(t, DcLinks.Of(http), time.GetUtcNow()))))).WithAction<DcReadLongTask>();
    }
}
