using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Core.Spaces;
using Kb.Data;
using Kb.Plugins;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;

namespace Kb.Web;

/// <summary>What the page actions share. Nothing here answers HTTP: a check gives a <see cref="Problem"/>, a write an <see cref="Outcome{T}"/>.</summary>
internal static class PageSupport
{
    private const int MaxContentChars = 5_000_000;

    /// <summary>Writes a new version of a page (or renames a folder), re-indexes it and clears the author's draft.</summary>
    private static bool OfPage(object row, Guid page) => row switch
    {
        Page p => p.Id == page,
        PageVersion v => v.PageId == page,
        PageTag t => t.PageId == page,
        PageDraft d => d.PageId == page,
        SearchTerm s => s.PageId == page,
        _ => false,
    };

    internal static async Task<Outcome<PageDto>> PublishAsync(KbDbContext db, IAccess access, DocRenderers renderers, Actor actor, Page page, string contentJson, bool major, string? note, IEnumerable<string> tags, DateTimeOffset now)
    {
        if (actor.UserId is not { } me) return Problem.Unauthorized; // a version names its author
        page.UpdatedBy = me;
        page.UpdatedAt = now;
        if (page.Kind == PageKind.Page)
        {
            page.ContentJson = contentJson;
            if (major) { page.Major++; page.Minor = 0; } else page.Minor++;
            db.PageVersions.Add(new PageVersion { PageId = page.Id, Major = page.Major, Minor = page.Minor, Title = page.Title, ContentJson = page.ContentJson, AuthorId = me, At = now, Note = note });
        }
        db.PageDrafts.RemoveRange(db.PageDrafts.Where(d => d.PageId == page.Id && d.UserId == me));
        await SearchIndex.StageAsync(db, page, tags, renderers);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            // A concurrent save took this version number. What was changed for this page is let go of, so that whatever the session
            // writes next (an audit entry) does not carry the failed save with it. Only this page's rows: a hook may have sent this
            // save from inside another action, whose handler still holds rows of its own.
            foreach (var entry in db.ChangeTracker.Entries().Where(e => OfPage(e.Entity, page.Id)).ToList()) entry.State = EntityState.Detached;
            return new Problem(409);
        }
        await ImageEndpoints.KeepAsync(db, page.ContentJson, page.SpaceId);
        return await ToDtoAsync(db, access, actor, page);
    }

    internal static async Task<int> NextSortAsync(KbDbContext db, Guid spaceId, Guid? parentId) =>
        (await db.Pages.Where(p => p.SpaceId == spaceId && p.ParentId == parentId).MaxAsync(p => (int?)p.Sort) ?? 0) + 1;

    /// <summary>The item and everything under it, deleted items included, tracked for update.</summary>
    internal static async Task<List<Page>> SubtreeAsync(KbDbContext db, Page root)
    {
        var links = await db.Pages.IgnoreQueryFilters().Where(p => p.SpaceId == root.SpaceId).Select(p => new { p.Id, p.ParentId }).ToListAsync();
        var children = links.Where(l => l.ParentId is not null).ToLookup(l => l.ParentId!.Value, l => l.Id);
        var ids = new List<Guid> { root.Id };
        for (var i = 0; i < ids.Count; i++) ids.AddRange(children[ids[i]]);
        return await db.Pages.IgnoreQueryFilters().Where(p => ids.Contains(p.Id)).ToListAsync();
    }

    /// <summary>Contributors move and delete their own work; other people's needs a Supervisor.</summary>
    internal static Task<bool> CanRemoveAsync(IAccess access, Actor actor, Guid spaceId, IEnumerable<Page> items) =>
        access.CanAsync(actor, spaceId, items.All(p => p.CreatedBy == actor.UserId) ? SpaceAction.MoveDeleteOwn : SpaceAction.MoveDeleteOthers);

    /// <param name="within">With no one space named: the spaces whose bins are listed. The caller's visible spaces.</param>
    internal static async Task<List<BinItemDto>> BinAsync(KbDbContext db, Guid? spaceId, bool includeHidden, IReadOnlyCollection<Guid>? within = null)
    {
        var roots = await db.Pages.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.DeletedAt != null && p.DeleteRootId == p.Id && (spaceId == null || p.SpaceId == spaceId) && (includeHidden || !p.BinHidden))
            .Where(p => within == null || within.Contains(p.SpaceId))
            .OrderByDescending(p => p.DeletedAt).Take(500)
            .Select(p => new { p.Id, p.SpaceId, p.Kind, p.Title, p.ParentId, p.DeletedAt, p.DeletedBy, p.BinHidden }).ToListAsync();
        var rootIds = roots.Select(r => r.Id).ToList();
        var spaceIds = roots.Select(r => r.SpaceId).Distinct().ToList();
        var counts = await db.Pages.IgnoreQueryFilters().Where(p => p.DeleteRootId != null && rootIds.Contains(p.DeleteRootId.Value))
            .GroupBy(p => p.DeleteRootId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.N);
        var spaces = await db.Spaces.AsNoTracking().Where(s => spaceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name);
        var folders = await db.Pages.IgnoreQueryFilters().AsNoTracking().Where(p => spaceIds.Contains(p.SpaceId) && p.Kind == PageKind.Folder)
            .Select(p => new { p.Id, p.ParentId, p.Title }).ToDictionaryAsync(p => p.Id);
        var userIds = roots.Select(r => r.DeletedBy).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);

        return roots.Select(r =>
        {
            var path = new List<string>();
            var parent = r.ParentId;
            for (var i = 0; parent is { } pid && i < 50 && folders.TryGetValue(pid, out var f); i++) { path.Insert(0, f.Title); parent = f.ParentId; }
            var space = spaces.GetValueOrDefault(r.SpaceId, "");
            return new BinItemDto(r.Id, r.SpaceId, space, r.Kind.ToString(), r.Title, string.Join(" / ", path.Prepend(space)), counts.GetValueOrDefault(r.Id, 1),
                r.DeletedAt!.Value, r.DeletedBy is { } by ? users.GetValueOrDefault(by, "Unknown") : "Unknown", r.BinHidden);
        }).ToList();
    }

    internal static async Task<PageVersion?> VersionAsync(KbDbContext db, Guid pageId, string version)
    {
        var parts = version.Split('.');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)) return null;
        return await db.PageVersions.AsNoTracking().FirstOrDefaultAsync(v => v.PageId == pageId && v.Major == major && v.Minor == minor);
    }

    internal static async Task<List<VersionDto>> HistoryAsync(KbDbContext db, Guid pageId, int take)
    {
        var rows = await (from v in db.PageVersions.AsNoTracking()
                          where v.PageId == pageId
                          orderby v.Major descending, v.Minor descending
                          join u in db.Users on v.AuthorId equals u.Id into au
                          from u in au.DefaultIfEmpty()
                          select new { v.Major, v.Minor, v.At, Author = u == null ? "Unknown" : u.DisplayName, v.Note })
                         .Take(take).ToListAsync();
        return rows.Select(h => new VersionDto($"{h.Major}.{h.Minor}", h.At, h.Author, h.Note)).ToList();
    }

    internal static Problem? TitleError(string? title) =>
        string.IsNullOrWhiteSpace(title) || title.Trim().Length > 200 ? Problem.Invalid("title", "1-200 characters.") : null;

    /// <summary>Trimmed, de-duplicated (case-insensitive) tags; null when any tag is invalid.</summary>
    internal static List<string>? CleanTags(IEnumerable<string>? tags)
    {
        var list = (tags ?? []).Select(t => t?.Trim() ?? "").Where(t => t.Length > 0).DistinctBy(t => t.ToLowerInvariant()).ToList();
        return list.Count <= 20 && list.All(t => t.Length <= 50 && !t.Contains(',')) ? list.Select(t => t.ToLowerInvariant()).ToList() : null;
    }

    internal static Problem TagError() => Problem.Invalid("tags", "Up to 20 tags, 1-50 characters each, no commas.");

    internal static bool IsEditorDoc(string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > MaxContentChars) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "doc";
        }
        catch (JsonException) { return false; }
    }

    internal static async Task<PageDto> ToDtoAsync(KbDbContext db, IAccess access, Actor actor, Page page)
    {
        var me = actor.UserId;
        var path = new List<CrumbDto>();
        var parentId = page.ParentId;
        for (var i = 0; parentId is { } pid && i < 50; i++)
        {
            var parent = await db.Pages.IgnoreQueryFilters().AsNoTracking().Where(p => p.Id == pid).Select(p => new { p.Id, p.Title, p.ParentId }).FirstAsync();
            path.Insert(0, new CrumbDto(parent.Id, parent.Title));
            parentId = parent.ParentId;
        }

        var tags = await db.PageTags.AsNoTracking().Where(t => t.PageId == page.Id).OrderBy(t => t.Tag).Select(t => t.Tag).ToListAsync();
        var updatedBy = await db.Users.Where(u => u.Id == page.UpdatedBy).Select(u => u.DisplayName).FirstOrDefaultAsync() ?? "Unknown";
        var draft = await (from d in db.PageDrafts.AsNoTracking()
                           where d.PageId == page.Id
                           join u in db.Users on d.UserId equals u.Id
                           select new { d.UserId, u.DisplayName, d.UpdatedAt }).FirstOrDefaultAsync();
        var files = await db.Attachments.AsNoTracking().Where(a => a.PageId == page.Id && a.DeletedAt == null).OrderBy(a => a.FileName)
            .Select(a => new AttachmentDto(a.Id, a.FileName, a.Size, "/api/v1/attachments/" + a.Id, a.CreatedAt)).ToListAsync();
        var canEdit = await access.CanAsync(actor, page.SpaceId, SpaceAction.CreateEdit);
        // Folders: whether their contents allow it is checked on the delete itself.
        var canDelete = await access.CanAsync(actor, page.SpaceId, page.CreatedBy == me ? SpaceAction.MoveDeleteOwn : SpaceAction.MoveDeleteOthers);

        return new PageDto(page.Id, page.SpaceId, page.ParentId, page.Kind.ToString(), page.Title, page.ContentJson, page.Version,
            page.UpdatedAt, updatedBy, path, await HistoryAsync(db, page.Id, 10), canEdit, tags, canDelete,
            draft is null ? null : new DraftInfoDto(draft.UserId == me, draft.DisplayName, draft.UpdatedAt), files, await UploadLimits.AttachmentBytesAsync(db),
            await Kb.Web.Actions.InventorySupport.PageDetailsAsync(db, page, CancellationToken.None));
    }
}
