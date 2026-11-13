using Kb.Core.Content;
using Kb.Core.Search;
using Kb.Core.Spaces;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web;

internal static class Guard
{
    public static Task<bool> IsAdminAsync(this KbDbContext db, Guid userId) =>
        db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.IsAdmin && u.DeactivatedAt == null);

    public static RouteGroupBuilder AdminGroup(this RouteGroupBuilder api) => api.MapGroup("/admin").AddEndpointFilter(async (ctx, next) =>
        await ctx.HttpContext.RequestServices.GetRequiredService<KbDbContext>().IsAdminAsync(ctx.HttpContext.User.UserId()) ? await next(ctx) : Results.StatusCode(403));
}
