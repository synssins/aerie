using Kb.Plugins;
using Kb.Plugins.Actions;

namespace Kb.Core.Spaces;

/// <summary>
/// The permission entry point handlers and plugins use. A person is answered by the registered <see cref="ISpacePermissionService"/>,
/// which already narrows a request made with an API token. A plugin doing background work is Owner of every space that is not sensitive
/// and has nothing in a sensitive one. Nobody has nothing.
/// </summary>
public sealed class SpaceAccess(ISpacePermissionService permissions, ISpaceAccessData data) : IAccess
{
    public async Task<SpaceRole?> RoleAsync(Actor actor, Guid spaceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor switch
        {
            { Kind: ActorKind.User, UserId: { } user } => await permissions.GetRoleAsync(user, spaceId, ct),
            { Kind: ActorKind.Plugin } => await data.GetSpaceAsync(spaceId, ct) is { IsSensitive: false } ? SpaceRole.Owner : null,
            _ => null,
        };
    }

    public async Task<bool> CanAsync(Actor actor, Guid spaceId, SpaceAction action, CancellationToken ct = default) =>
        await RoleAsync(actor, spaceId, ct) is { } role && role >= RoleMatrix.MinimumRole(action);

    public async Task<IReadOnlyList<Guid>> VisibleSpacesAsync(Actor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor switch
        {
            { Kind: ActorKind.User, UserId: { } user } => await permissions.VisibleSpaceIdsAsync(user, ct),
            { Kind: ActorKind.Plugin } => [.. (await data.GetAllSpacesAsync(ct)).Where(s => !s.IsSensitive).Select(s => s.Id)],
            _ => [],
        };
    }

    public async Task<bool> IsAdminAsync(Actor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        // A request made with an API token is never an Admin's, whoever owns the token.
        return actor is { Kind: ActorKind.User, UserId: { } user, TokenId: null } && await data.GetPrincipalAsync(user, ct) is { IsAdmin: true };
    }
}
