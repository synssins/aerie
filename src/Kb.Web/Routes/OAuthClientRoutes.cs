using Kb.Web.Actions;

namespace Kb.Web;

/// <summary>Administration of OAuth 2.0 clients and of the compatible surfaces' sign-in settings (contracts/aerie/openapi.yaml, oauth-clients).</summary>
public static class OAuthClientRoutes
{
    public static void MapOAuthClientEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin"); // no filter: each handler answers 403 to a caller who is not an Admin

        admin.MapGet("/oauth/clients", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ListOAuthClients(), ct).ToHttp()).WithAction<ListOAuthClients>();

        admin.MapPost("/oauth/clients", (OAuthClientRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new CreateOAuthClient { Request = req }, ct).ToHttp(made => Results.Created($"/api/v1/admin/oauth/clients/{made.Client.Id}", made))).WithAction<CreateOAuthClient>();

        admin.MapGet("/oauth/clients/{id:guid}", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadOAuthClient { Id = id }, ct).ToHttp()).WithAction<ReadOAuthClient>();

        admin.MapPut("/oauth/clients/{id:guid}", (Guid id, OAuthClientRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new UpdateOAuthClient { Id = id, Request = req }, ct).ToHttp()).WithAction<UpdateOAuthClient>();

        admin.MapDelete("/oauth/clients/{id:guid}", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new DeleteOAuthClient { Id = id }, ct).ToHttp()).WithAction<DeleteOAuthClient>();

        admin.MapPost("/oauth/clients/{id:guid}/secret", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new RotateOAuthClientSecret { Id = id }, ct).ToHttp()).WithAction<RotateOAuthClientSecret>();

        admin.MapGet("/auth/compat", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadCompatAuthSettings(), ct).ToHttp()).WithAction<ReadCompatAuthSettings>();

        admin.MapPut("/auth/compat", (CompatAuthSettingsDto req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SaveCompatAuthSettings { Settings = req }, ct).ToHttp()).WithAction<SaveCompatAuthSettings>();
    }
}
