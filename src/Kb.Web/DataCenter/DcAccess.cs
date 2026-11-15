using Kb.Core.Identity;
using Kb.Core.Spaces;

namespace Kb.Web.DataCenter;

/// <summary>
/// Aerie's permission front (<see cref="SpaceAccess"/>), with one difference on the Data Center compatible surface: a personal token acts with
/// its owner's role there (contracts/behaviour/identity.md, Personal access tokens and Scopes), so an Admin's token is an Admin's. Everywhere
/// else, and for service tokens, a request made with a token is never an Admin's, as before. Write scopes are checked by the method rule
/// (ApiTokenAuth.EnforceAsync) before any handler runs.
/// </summary>
public sealed class DcAccess(SpaceAccess inner, IHttpContextAccessor http, ISpaceAccessData data) : IAccess
{
    public Task<SpaceRole?> RoleAsync(Actor actor, Guid spaceId, CancellationToken ct = default) => inner.RoleAsync(actor, spaceId, ct);
    public Task<bool> CanAsync(Actor actor, Guid spaceId, SpaceAction action, CancellationToken ct = default) => inner.CanAsync(actor, spaceId, action, ct);
    public Task<bool> CanAsync(Actor actor, string permission, Guid? spaceId = null, CancellationToken ct = default) => inner.CanAsync(actor, permission, spaceId, ct);
    public Task<IReadOnlyList<Guid>> VisibleSpacesAsync(Actor actor, CancellationToken ct = default) => inner.VisibleSpacesAsync(actor, ct);

    public async Task<bool> IsAdminAsync(Actor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (await inner.IsAdminAsync(actor, ct)) return true;
        return actor is { Kind: ActorKind.User, UserId: { } user, TokenId: { } tokenId } && DcRest.IsSurfaceRoute(http.HttpContext)
            && ApiTokenAuth.Current(http.HttpContext) is { Kind: TokenKind.Personal } token && token.Id == tokenId && token.OwnerUserId == user
            && await data.GetPrincipalAsync(user, ct) is { IsAdmin: true };
    }
}
