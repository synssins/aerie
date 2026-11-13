using Kb.Core.Plugins;
using Kb.Core.Spaces;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web;

/// <summary>The global permissions loaded plugins declare, and who holds them. (Whether one actor holds one permission is <see cref="IAccess.CanAsync(Actor, string, Guid?, CancellationToken)"/>.)</summary>
internal static class PluginPermissions
{
    /// <summary>Every global permission a loaded plugin declares, as "&lt;plugin id&gt;:&lt;key&gt;".</summary>
    public static List<string> Global(PluginRegistry registry) =>
        [.. registry.Loaded.SelectMany(p => p.Manifest.Permissions.Where(x => x.Scope == PluginPermissionScopes.Global).Select(x => $"{p.Manifest.Id}:{x.Key}"))];

    /// <summary>For each user who was given one, the global permissions that still exist.</summary>
    public static async Task<ILookup<Guid, string>> GrantsAsync(KbDbContext db, PluginRegistry registry, CancellationToken ct)
    {
        var declared = Global(registry);
        if (declared.Count == 0) return Array.Empty<(Guid, string)>().ToLookup(g => g.Item1, g => g.Item2);
        return (await db.UserPermissions.AsNoTracking().Where(p => declared.Contains(p.Permission)).ToListAsync(ct)).ToLookup(p => p.UserId, p => p.Permission);
    }

    /// <summary>An Admin holds every one; anyone else those an Admin gave them.</summary>
    public static async Task<IReadOnlyList<string>> HeldAsync(KbDbContext db, PluginRegistry registry, Guid userId, bool isAdmin, CancellationToken ct) =>
        isAdmin ? Global(registry) : [.. (await GrantsAsync(db, registry, ct))[userId].Order(StringComparer.Ordinal)];
}
