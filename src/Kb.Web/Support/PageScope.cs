using Kb.Core.Content;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web;

/// <summary>
/// The spaces an actor may read pages in. Every page query made for a caller goes through one (<see cref="PageScopes.In"/>,
/// <see cref="PageScopes.FindVisibleAsync"/>), so a rule added here holds for every action.
/// </summary>
internal sealed record PageScope(IReadOnlyList<Guid> SpaceIds);

internal static class PageScopes
{
    /// <summary>The actor's spaces; with <paramref name="within"/>, only those of them the actor has a role in. None means 404.</summary>
    public static async Task<PageScope> ForAsync(IAccess access, Actor actor, IReadOnlyList<Guid>? within, CancellationToken ct)
    {
        if (within is null) return new(await access.VisibleSpacesAsync(actor, ct));
        var mine = new List<Guid>();
        foreach (var id in within)
            if (await access.RoleAsync(actor, id, ct) is not null) mine.Add(id);
        return new(mine);
    }

    public static IQueryable<Page> In(this IQueryable<Page> pages, PageScope scope)
    {
        var spaces = scope.SpaceIds;
        return pages.Where(p => spaces.Contains(p.SpaceId));
    }

    /// <summary>The page, when the actor has a role in its space. Null means 404: a page the actor cannot see answers like one that does not exist.</summary>
    public static async Task<Page?> FindVisibleAsync(this KbDbContext db, IAccess access, Actor actor, Guid pageId, bool track, CancellationToken ct)
    {
        var page = await (track ? db.Pages : db.Pages.AsNoTracking()).FirstOrDefaultAsync(p => p.Id == pageId, ct);
        return page is not null && await access.RoleAsync(actor, page.SpaceId, ct) is not null ? page : null;
    }
}
