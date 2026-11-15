using System.Globalization;
using System.Text.Json.Nodes;
using Kb.Core.Config;
using Kb.Web.Actions;
using Microsoft.Extensions.Options;

namespace Kb.Web.DataCenter;

public static class DcRoutes
{
    /// <summary>
    /// The Data Center compatible surface, beside aerie's own /api/v1. Every route is open to anyone at the routing level: each operation decides
    /// who may call it, because the contract answers 401, 403 or the anonymous user depending on the operation. API tokens may call every route
    /// (ApiTokenAuth.EnforceAsync); each one decides access through the permission service like the rest of aerie.
    /// </summary>
    public static void MapDataCenterApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = app.Services.GetRequiredService<IOptions<KbOptions>>().Value.DataCenter;
        var dc = app.MapGroup(options.ContextPath + DcRest.Root).AllowAnonymous().WithMetadata(DcRoute.Marker);
        dc.MapDcUsers();

        dc.MapGet("/server-information", (HttpContext http) =>
        {
            if (http.User.Identity?.IsAuthenticated != true) return DcRest.Fail(401, "Sign in to read server information.");
            var links = DcLinks.Of(http);
            return DcRest.Json(new JsonObject
            {
                ["baseUrl"] = links.Base,
                ["version"] = options.Version,
                ["buildNumber"] = options.BuildNumber > 0 ? options.BuildNumber : Derived(options.Version),
                ["marketplaceBuildNumber"] = options.MarketplaceBuildNumber > 0 ? options.MarketplaceBuildNumber : Derived(options.Version),
                ["buildDate"] = options.BuildDate,
            });
        });

        // The picture user objects point at (DcUserJson.AvatarPath). Outside /rest/api: it is a file, not an operation.
        app.MapGet(options.ContextPath + DcUserJson.AvatarPath, () => Results.Text(DcUserJson.AvatarSvg, "image/svg+xml")).AllowAnonymous();
    }

    /// <summary>
    /// A build number made from the version (9.2.4 is 9002004) until the spec room states the real table.
    /// See docs/spec-questions/server-information-build-numbers.md.
    /// </summary>
    internal static int Derived(string version)
    {
        var parts = version.Split('.').Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0).Concat([0, 0, 0]).Take(3).ToArray();
        return parts[0] * 1_000_000 + parts[1] * 1_000 + parts[2];
    }
}

public static class DcUserRoutes
{
    public static void MapDcUsers(this RouteGroupBuilder dc)
    {
        dc.MapGet("/user", (HttpContext http, string? key, string? username, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new DcReadUser { Key = key, UserName = username }, ct).Rest(u => DcRest.Json(DcLinks.Of(http).Top(DcUserJson.Of(u, DcLinks.Of(http), Expand.Of(http.Request))))))
            .WithAction<DcReadUser>();

        dc.MapGet("/user/anonymous", (HttpContext http) => DcRest.Json(DcLinks.Of(http).Top(DcUserJson.Anonymous(DcLinks.Of(http)))));

        // Someone not signed in is the anonymous user here, not a 401.
        dc.MapGet("/user/current", (HttpContext http, IActionBus bus, CancellationToken ct) =>
        {
            var links = DcLinks.Of(http);
            return http.User.Identity?.IsAuthenticated != true
                ? Task.FromResult(DcRest.Json(links.Top(DcUserJson.Anonymous(links))))
                : bus.SendAsync(new ReadMe(), ct).Rest(me => DcRest.Json(links.Top(DcUserJson.Of(new DcUser(me.Id, me.UserName, me.DisplayName, false, false), links, Expand.Of(http.Request)))));
        }).WithAction<ReadMe>();

        dc.MapGet("/user/list", (HttpContext http, IActionBus bus, CancellationToken ct) =>
        {
            var links = DcLinks.Of(http);
            if (Paging.Read(http.Request, 100, links.MaxLimit) is not { } paging) return Task.FromResult(Paging.Invalid);
            var expand = Expand.Of(http.Request);
            return bus.SendAsync(new DcListUsers { Start = paging.Start, Limit = paging.Limit }, ct)
                .Rest(page => DcRest.Json(links.Page(paging, page.Users.Select(u => DcUserJson.Of(u, links, expand)), page.More)));
        }).WithAction<DcListUsers>();

        dc.MapGet("/user/memberof", (HttpContext http, string? key, string? username, IActionBus bus, CancellationToken ct) =>
        {
            var links = DcLinks.Of(http);
            if (Paging.Read(http.Request, 200, links.MaxLimit) is not { } paging) return Task.FromResult(Paging.Invalid);
            return bus.SendAsync(new DcUserGroups { Key = key, UserName = username, Start = paging.Start, Limit = paging.Limit }, ct)
                .Rest(page => DcRest.Json(links.Page(paging, page.Names.Select(n => (JsonNode)new JsonObject
                {
                    ["type"] = "group",
                    ["name"] = n,
                    ["_links"] = new JsonObject { ["self"] = links.Abs("/rest/api/group/" + Uri.EscapeDataString(n)) },
                }), page.More)));
        }).WithAction<DcUserGroups>();
    }
}
