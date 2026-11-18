using Kb.Core.Audit;
using Kb.Core.Identity;
using Kb.Core.Spaces;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web;

/// <summary>A service token may name a sensitive space only when someone who manages that space made it.</summary>
internal static class ServiceTokenRules
{
    /// <summary>
    /// At start: revokes each service token that names a sensitive space and was made by someone who does not manage the space. Such a
    /// token is from before the space was sensitive (an Admin could then name any space); the space's Owners can neither see nor revoke it.
    /// </summary>
    public static async Task RevokeStraysAsync(KbDbContext db, ISpacePermissionService perms, IAuditLog audit, DateTimeOffset now)
    {
        var sensitive = (await db.Spaces.AsNoTracking().Where(s => s.IsSensitive).Select(s => s.Id).ToListAsync()).ToHashSet();
        if (sensitive.Count == 0) return;
        foreach (var token in await db.ApiTokens.Where(t => t.Kind == TokenKind.Service && t.RevokedAt == null).ToListAsync())
            foreach (var space in token.Scopes().Select(s => s.SpaceId).OfType<Guid>().Where(sensitive.Contains).Distinct())
            {
                if (await perms.CanAsync(token.CreatedBy, space, SpaceAction.ManageRolesSettingsSensitive)) continue;
                token.RevokedAt = now;
                await db.SaveChangesAsync();
                await audit.AppendAsync(new AuditEvent(AuditActions.TokenRevoke, null, "token", token.Id.ToString(), space,
                    new Dictionary<string, string> { ["name"] = token.Name, ["reason"] = "names a sensitive space and was not made by someone who manages it" }));
                break;
            }
    }
}
