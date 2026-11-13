using Kb.Plugins;
using Kb.Plugins.Actions;

namespace Kb.Core.Spaces;

/// <summary>
/// The permission entry point handlers and plugins use. A person is answered by the registered <see cref="ISpacePermissionService"/>,
/// which already narrows a request made with an API token. A plugin doing background work is Owner of every space that is not sensitive
/// and has nothing in a sensitive one. Nobody has nothing.
/// </summary>
/// <summary>What <see cref="SpaceAccess"/> needs to know about plugin permissions an Admin changed or granted.</summary>
public interface IPluginPermissionData
{
    /// <summary>The minimum role an Admin set for a space permission, or null when the manifest's default holds.</summary>
    Task<SpaceRole?> GetMinRoleAsync(string permission, CancellationToken ct);
    Task<bool> IsGrantedAsync(Guid userId, string permission, CancellationToken ct);
}

public sealed class SpaceAccess(ISpacePermissionService permissions, ISpaceAccessData data, IPluginPermissionData pluginPermissions, Kb.Core.Plugins.PluginRegistry plugins) : IAccess
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

    public async Task<bool> CanAsync(Actor actor, string permission, Guid? spaceId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(permission);
        var cut = permission.IndexOf(':', StringComparison.Ordinal);
        if (cut <= 0) return false;
        var (pluginId, key) = (permission[..cut], permission[(cut + 1)..]);
        // Only a permission that a loaded plugin declares exists. A grant or a role for a plugin that is gone or disabled counts for nothing.
        if (plugins.Find(pluginId) is not { Loaded: true } plugin || plugin.Manifest.Permissions.FirstOrDefault(p => p.Key == key) is not { } declared) return false;

        if (declared.Scope == PluginPermissionScopes.Global)
        {
            // A person an Admin gave it to, or an Admin. Never a request made with an API token, a plugin's background work, or nobody.
            // The principal is read first, as for a role in a space: a deactivated account holds nothing.
            return actor is { Kind: ActorKind.User, UserId: { } user, TokenId: null } && await data.GetPrincipalAsync(user, ct) is { } principal
                && (principal.IsAdmin || await pluginPermissions.IsGrantedAsync(user, permission, ct));
        }
        if (declared.Scope != PluginPermissionScopes.Space || spaceId is not { } space) return false;
        var minimum = await pluginPermissions.GetMinRoleAsync(permission, ct) ?? PluginPermissionScopes.DefaultRole(declared);
        return await RoleAsync(actor, space, ct) is { } role && role >= minimum;
    }

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

/// <summary>The two scopes of a plugin permission, and the role a space permission starts with.</summary>
public static class PluginPermissionScopes
{
    public const string Space = "space", Global = "global";

    /// <summary>The manifest's default role; Owner when the manifest names none that exists.</summary>
    public static SpaceRole DefaultRole(PluginPermission permission)
    {
        ArgumentNullException.ThrowIfNull(permission);
        return Enum.TryParse<SpaceRole>(permission.DefaultRole, out var role) && Enum.IsDefined(role) ? role : SpaceRole.Owner;
    }
}
