using Kb.Core.Identity;
using Kb.Core.Spaces;

namespace Kb.Web.DataCenter;

/// <summary>
/// Aerie's permission front (<see cref="SpaceAccess"/>), with one difference on the Data Center compatible surface: a personal token acts with
/// its owner's role there (contracts/behaviour/identity.md, Personal access tokens and Scopes), so an Admin's token is an Admin's. Everywhere
/// else, and for service tokens, a request made with a token is never an Admin's, as before. Write scopes are checked by the method rule
/// (ApiTokenAuth.EnforceAsync) before any handler runs.
/// Two callers are held to what they were given: a personal token limited to named spaces is not an Admin's (the site is more than those
/// spaces), and an OAuth token is an Admin's only when it holds the ADMIN scope (what the consent screen named is the most it does).
/// </summary>
public sealed class DcAccess(SpaceAccess inner, IHttpContextAccessor http, ISpaceAccessData data) : IAccess
{
    public Task<SpaceRole?> RoleAsync(Actor actor, Guid spaceId, CancellationToken ct = default) => inner.RoleAsync(actor, spaceId, ct);
    public Task<bool> CanAsync(Actor actor, Guid spaceId, SpaceAction action, CancellationToken ct = default) => inner.CanAsync(actor, spaceId, action, ct);
    public Task<bool> CanAsync(Actor actor, string permission, Guid? spaceId = null, CancellationToken ct = default) => inner.CanAsync(actor, permission, spaceId, ct);
    public Task<IReadOnlyList<Guid>> VisibleSpacesAsync(Actor actor, CancellationToken ct = default) => inner.VisibleSpacesAsync(actor, ct);
    public Task<bool> SeesHistoryAsync(Actor actor, Guid spaceId, CancellationToken ct = default) => inner.SeesHistoryAsync(actor, spaceId, ct);

    public async Task<bool> IsAdminAsync(Actor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (ApiTokenAuth.OAuthCurrent(http.HttpContext) is { } oauth
            && !OAuthScopes.Expand(oauth.Token.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Contains(OAuthScopes.Admin)) return false;
        if (await inner.IsAdminAsync(actor, ct)) return true;
        return actor is { Kind: ActorKind.User, UserId: { } user, TokenId: { } tokenId } && DcRest.IsSurfaceRoute(http.HttpContext)
            && ApiTokenAuth.Current(http.HttpContext) is { Kind: TokenKind.Personal } token && token.Id == tokenId && token.OwnerUserId == user
            && token.Scopes().Any(s => s.SpaceId is null)
            && await data.GetPrincipalAsync(user, ct) is { IsAdmin: true };
    }
}
