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
            var r = await bus.SendAsync(new DcOAuthAuthorize { Query = query }, ct);
            if (!r.Ok) return Results.StatusCode(r.Problem!.Status);
            // Nothing here is cached: every answer names a one-time request or code.
            http.Response.Headers.CacheControl = "no-store";
            return r.Value!.ErrorPage is { } page ? Results.Content(page, "text/html; charset=utf-8", statusCode: StatusCodes.Status400BadRequest) : Results.Redirect(r.Value.Location!);
        }).WithAction<DcOAuthAuthorize>();
    }

    public static void MapOAuthConsentEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/oauth/authorize-requests/{id:guid}", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadOAuthAuthorizeRequest { Id = id }, ct).ToHttp()).WithAction<ReadOAuthAuthorizeRequest>();

        api.MapPost("/oauth/authorize-requests/{id:guid}/decision", (Guid id, OAuthDecisionRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new DecideOAuthAuthorizeRequest { Id = id, Allow = req.Allow }, ct).ToHttp()).WithAction<DecideOAuthAuthorizeRequest>();
    }
}
