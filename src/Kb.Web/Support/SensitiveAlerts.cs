using Kb.Core.Access;
using Kb.Core.Notifications;
using Kb.Core.Spaces;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web;

/// <summary>
/// Admins have no silent way into a sensitive space. An Admin's action that gives someone a role there by another road (a group, an
/// account's password) goes ahead, and the space's Owners are told, as they are of emergency access.
/// </summary>
internal static class SensitiveAlerts
{
    /// <summary>Alerts the Owners of every sensitive space in which one of these users or groups holds a role.</summary>
    public static async Task SendAsync(KbDbContext db, IServiceProvider services, IReadOnlyCollection<string> principalIds, Func<Space, string> title, string body)
    {
        var spaces = await db.SpaceRoleAssignments.AsNoTracking().Where(a => principalIds.Contains(a.PrincipalId))
            .Join(db.Spaces.AsNoTracking().Where(s => s.IsSensitive), a => a.SpaceId, s => s.Id, (a, s) => s).Distinct().ToListAsync(CancellationToken.None);
        foreach (var s in spaces)
            await services.GetRequiredService<INotifier>().NotifyAsync(await services.GetRequiredService<IAccessData>().GetOwnerUserIdsAsync(s.Id, CancellationToken.None),
                NotificationKinds.SecurityAlert, title(s), body, $"/spaces/{s.Id}", alsoEmail: true, CancellationToken.None);
    }
}
