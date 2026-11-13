using Kb.Core.Identity;
using Kb.Web.Actions;

namespace Kb.Web;

public sealed record CreateTokenRequest(string? Name, TokenKind Kind, IReadOnlyList<TokenScope>? Scopes, int? Days, Guid? AccountId = null);

public static class TokenRoutes
{
    public static void MapTokenEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/tokens", (CreateTokenRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new CreateToken { Name = req.Name, Kind = req.Kind, Scopes = req.Scopes, Days = req.Days, AccountId = req.AccountId }, ct)
                .ToHttp(made => Results.Created($"/api/v1/tokens/{made.Info.Id}", made))).WithAction<CreateToken>();

        api.MapGet("/tokens", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ListMyTokens(), ct).ToHttp()).WithAction<ListMyTokens>();

        api.MapDelete("/tokens/{id:guid}", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new RevokeToken { TokenId = id }, ct).ToHttp()).WithAction<RevokeToken>();

        var admin = api.MapGroup("/admin"); // no filter: each handler answers 403 to a caller who is not an Admin

        admin.MapGet("/tokens", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ListAllTokens(), ct).ToHttp()).WithAction<ListAllTokens>();

        admin.MapGet("/tokens/settings", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadTokenSettings(), ct).ToHttp()).WithAction<ReadTokenSettings>();

        admin.MapPut("/tokens/settings", (TokenSettingsDto req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SaveTokenSettings { MaxDays = req.MaxDays }, ct).ToHttp()).WithAction<SaveTokenSettings>();
    }
}
