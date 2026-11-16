using Kb.Core.Config;
using Kb.Web.Actions;
using Kb.Web.DataCenter;
using Microsoft.Extensions.Options;

namespace Kb.Web;

/// <summary>
/// OAuth 2.0 on the compatible surface (contracts/datacenter/openapi.yaml, tag oauth2), under the context prefix like <c>/rest/api</c>,
/// and the consent page's two calls on aerie's own API.
/// </summary>
public static class OAuthRoutes
{
    public static void MapDcOAuth(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var context = app.Services.GetRequiredService<IOptions<KbOptions>>().Value.DataCenter.ContextPath;
        var oauth = app.MapGroup(context + DcRest.OAuthRoot + "/latest").AllowAnonymous().WithMetadata(DcOAuthRoute.Marker);

        oauth.MapGet("/authorize", async (HttpContext http, IActionBus bus, CancellationToken ct) =>
        {
            var query = http.Request.Query.ToDictionary(kv => kv.Key, kv => kv.Value.Select(v => v ?? "").ToArray(), StringComparer.Ordinal);
            var r = await bus.SendAsync(new DcOAuthAuthorize { Query = query, SiteIsHttps = SiteIsHttps(http) }, ct);
            if (!r.Ok) return Results.StatusCode(r.Problem!.Status);
            // Nothing here is cached: every answer names a one-time request or code.
            http.Response.Headers.CacheControl = "no-store";
            return r.Value!.ErrorPage is { } page ? Results.Content(page, "text/html; charset=utf-8", statusCode: StatusCodes.Status400BadRequest) : Results.Redirect(r.Value.Location!);
        }).WithAction<DcOAuthAuthorize>();

        // Called by servers, not browsers: no XSRF header (identity.md, The token endpoint).
        oauth.MapPost("/token", async (HttpContext http, IActionBus bus, CancellationToken ct) =>
        {
            var (parameters, basic, badBasic, badBody) = await OAuthHttp.ReadAsync(http);
            if (badBody) return OAuthHttp.Write(http, new OAuthEndpointResult(400, new System.Text.Json.Nodes.JsonObject { ["error"] = "invalid_request", ["error_description"] = "The form body could not be read." }));
            var r = await bus.SendAsync(new DcOAuthToken { Parameters = parameters, Basic = basic, BadBasic = badBasic, Address = BasicThrottle.AddressOf(http), SiteIsHttps = SiteIsHttps(http) }, ct);
            return r.Ok ? OAuthHttp.Write(http, r.Value!) : Results.StatusCode(r.Problem!.Status);
        }).WithAction<DcOAuthToken>();

        oauth.MapPost("/revoke", async (HttpContext http, IActionBus bus, CancellationToken ct) =>
        {
            var (parameters, basic, badBasic, badBody) = await OAuthHttp.ReadAsync(http);
            if (badBody) return OAuthHttp.Write(http, new OAuthEndpointResult(400, new System.Text.Json.Nodes.JsonObject { ["error"] = "invalid_request", ["error_description"] = "The form body could not be read." }));
            var r = await bus.SendAsync(new DcOAuthRevoke { Parameters = parameters, Basic = basic, BadBasic = badBasic, Address = BasicThrottle.AddressOf(http), SiteIsHttps = SiteIsHttps(http) }, ct);
            return r.Ok ? OAuthHttp.Write(http, r.Value!) : Results.StatusCode(r.Problem!.Status);
        }).WithAction<DcOAuthRevoke>();

        // RFC 8414: at the site root; with a context prefix, also at the root followed by that prefix (section 3).
        IResult Metadata(HttpContext http)
        {
            var links = DcLinks.Of(http);
            return Results.Json(OAuthMetadata.Document(links.Base, links.Base));
        }
        app.MapGet("/.well-known/oauth-authorization-server", Metadata).AllowAnonymous().WithMetadata(DcOAuthRoute.Marker);
        if (context.Length > 0) app.MapGet("/.well-known/oauth-authorization-server" + context, Metadata).AllowAnonymous().WithMetadata(DcOAuthRoute.Marker);
    }

    /// <summary>The site's base URL as clients see it is https (the configured base URL, or the request's own).</summary>
    private static bool SiteIsHttps(HttpContext http) => DcLinks.Of(http).Base.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public static void MapOAuthConsentEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/me/oauth/grants", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ListMyOAuthGrants(), ct).ToHttp()).WithAction<ListMyOAuthGrants>();

        api.MapDelete("/me/oauth/grants/{id:guid}", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new RevokeMyOAuthGrant { Id = id }, ct).ToHttp()).WithAction<RevokeMyOAuthGrant>();

        api.MapGet("/oauth/authorize-requests/{id:guid}", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadOAuthAuthorizeRequest { Id = id }, ct).ToHttp()).WithAction<ReadOAuthAuthorizeRequest>();

        api.MapPost("/oauth/authorize-requests/{id:guid}/decision", (Guid id, OAuthDecisionRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new DecideOAuthAuthorizeRequest { Id = id, Allow = req.Allow }, ct).ToHttp()).WithAction<DecideOAuthAuthorizeRequest>();
    }
}
