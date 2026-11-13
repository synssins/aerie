using Kb.Core.Spaces;
using Kb.Plugins;
using Microsoft.EntityFrameworkCore;

namespace Kb.Data.Plugins;

public sealed class PluginPermissionData(KbDbContext db) : IPluginPermissionData
{
    public async Task<SpaceRole?> GetMinRoleAsync(string permission, CancellationToken ct) =>
        await db.PluginPermissionRoles.AsNoTracking().Where(p => p.Permission == permission).Select(p => (SpaceRole?)p.MinRole).FirstOrDefaultAsync(ct);

    public Task<bool> IsGrantedAsync(Guid userId, string permission, CancellationToken ct) =>
        db.UserPermissions.AsNoTracking().AnyAsync(p => p.UserId == userId && p.Permission == permission, ct);
}
