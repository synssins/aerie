using System.Globalization;
using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Data;
using Microsoft.EntityFrameworkCore;
using static Kb.Web.PageSupport;

namespace Kb.Web.Actions;

internal sealed class MovePageHandler(KbDbContext db, IAccess access) : IActionHandler<MovePage, PageDto>
{
    private Page? _page;
    private List<Page> _subtree = [];

    public async Task<Problem?> AuthorizeAsync(ActionContext<MovePage> ctx)
    {
        if ((_page = await db.FindVisibleAsync(access, ctx.Actor, ctx.Action.PageId, track: true, ctx.Cancel)) is null) return Problem.NotFound;
        var (from, target) = (_page.SpaceId, ctx.Action.SpaceId ?? _page.SpaceId);
        if (await access.RoleAsync(ctx.Actor, target, ctx.Cancel) is null) return Problem.NotFound;
        if (!await access.CanAsync(ctx.Actor, target, SpaceAction.CreateEdit, ctx.Cancel)) return Problem.Forbidden;
        _subtree = await SubtreeAsync(db, _page);
        if (!await CanRemoveAsync(access, ctx.Actor, from, _subtree)) return Problem.Forbidden;
        return ctx.Actor.UserId is null ? Problem.Unauthorized : null; // the audit entry names who moved it
    }

    public async Task<Outcome<PageDto>> HandleAsync(ActionContext<MovePage> ctx)
    {
        var (req, page, subtree) = (ctx.Action, _page!, _subtree);
        var from = page.SpaceId;
        var target = req.SpaceId ?? from;
        if (req.ParentId is { } pid)
        {
            if (subtree.Any(p => p.Id == pid)) return Problem.Invalid("parentId", "A folder can't move into itself.");
            if (!await db.Pages.AnyAsync(p => p.Id == pid && p.SpaceId == target && p.Kind == PageKind.Folder, ctx.Cancel)) // system: the parent must be a folder of the target space
                return Problem.Invalid("parentId", "Must be a folder in the target space.");
        }
        if (page.ParentId == req.ParentId && target == from) return await ToDtoAsync(db, access, ctx.Actor, page);

        page.Sort = await NextSortAsync(db, target, req.ParentId);
        page.ParentId = req.ParentId;
        if (target != from)
        {
            // Images are stored per space. Let the target space show the ones these pages use, now and in their history.
            var ids = subtree.Select(p => p.Id).ToList();
            var docs = subtree.Select(p => p.ContentJson).Concat(await db.PageVersions.Where(v => ids.Contains(v.PageId)).Select(v => v.ContentJson).ToListAsync());
            var images = docs.SelectMany(DocText.ImageIds).Distinct().ToList();
            var granted = await db.SpaceImageGrants.Where(g => g.SpaceId == target && images.Contains(g.ImageId)).Select(g => g.ImageId).ToListAsync();
            var foreign = await db.SpaceImages.Where(i => images.Contains(i.Id) && i.SpaceId != target).Select(i => i.Id).ToListAsync();
            db.SpaceImageGrants.AddRange(foreign.Except(granted).Select(i => new SpaceImageGrant { ImageId = i, SpaceId = target }));
            foreach (var p in subtree) p.SpaceId = target;
        }
        await db.SaveChangesAsync();
        ctx.Audit(AuditActions.PageMove, "page", page.Id.ToString(), target,
            new Dictionary<string, string> { ["fromSpace"] = from.ToString(), ["parent"] = req.ParentId?.ToString() ?? "" });
        return await ToDtoAsync(db, access, ctx.Actor, page);
    }
}

// Delete = move to the recycle bin. Nothing here destroys data.
internal sealed class DeletePageHandler(KbDbContext db, IAccess access, TimeProvider time) : IActionHandler<DeletePage, Unit>
{
    private Page? _page;
    private List<Page> _live = [];

    public async Task<Problem?> AuthorizeAsync(ActionContext<DeletePage> ctx)
    {
        if ((_page = await db.FindVisibleAsync(access, ctx.Actor, ctx.Action.PageId, track: true, ctx.Cancel)) is null) return Problem.NotFound;
        _live = (await SubtreeAsync(db, _page)).Where(p => p.DeletedAt is null).ToList();
        if (!await CanRemoveAsync(access, ctx.Actor, _page.SpaceId, _live)) return Problem.Forbidden;
        return ctx.Actor.UserId is null ? Problem.Unauthorized : null; // a deleted item names who deleted it
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<DeletePage> ctx)
    {
        var (page, live, me) = (_page!, _live, ctx.Actor.UserId!.Value);
        var now = time.GetUtcNow();
        foreach (var p in live) { p.DeletedAt = now; p.DeletedBy = me; p.DeleteRootId = page.Id; p.BinHidden = false; }
        await db.SaveChangesAsync();
        ctx.Audit(AuditActions.PageDelete, "page", page.Id.ToString(), page.SpaceId,
            new Dictionary<string, string> { ["title"] = page.Title, ["items"] = live.Count.ToString(CultureInfo.InvariantCulture) });
        return new Unit();
    }
}

internal sealed class ListBinHandler(KbDbContext db, IAccess access) : IActionHandler<ListBin, IReadOnlyList<BinItemDto>>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<ListBin> ctx) => BinChecks.ManagesAsync(access, ctx.Actor, ctx.Action.SpaceId, ctx.Cancel);

    public async Task<Outcome<IReadOnlyList<BinItemDto>>> HandleAsync(ActionContext<ListBin> ctx) =>
        Outcome.List(await BinAsync(db, ctx.Action.SpaceId, includeHidden: false));
}

// Hides the items from the space's bin. The admin bin keeps them.
internal sealed class EmptyBinHandler(KbDbContext db, IAccess access) : IActionHandler<EmptyBin, Unit>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<EmptyBin> ctx)
    {
        if (await BinChecks.ManagesAsync(access, ctx.Actor, ctx.Action.SpaceId, ctx.Cancel) is { } denied) return denied;
        return ctx.Actor.UserId is null ? Problem.Unauthorized : null; // the audit entry names who emptied the bin
    }

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<EmptyBin> ctx)
    {
        var id = ctx.Action.SpaceId;
        var n = await db.Pages.IgnoreQueryFilters().Where(p => p.SpaceId == id && p.DeletedAt != null && !p.BinHidden).ExecuteUpdateAsync(s => s.SetProperty(p => p.BinHidden, true)); // system: the bin's items are deleted pages; the caller manages this space's bin
        ctx.Audit(AuditActions.BinEmpty, "space", id.ToString(), id, new Dictionary<string, string> { ["items"] = n.ToString(CultureInfo.InvariantCulture) });
        return new Unit();
    }
}

internal sealed class RestorePageHandler(KbDbContext db, IAccess access, DocRenderers renderers) : IActionHandler<RestorePage, RestoredPageDto>
{
    private Page? _root;

    public async Task<Problem?> AuthorizeAsync(ActionContext<RestorePage> ctx)
    {
        var id = ctx.Action.PageId;
        _root = await db.Pages.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt != null && p.DeleteRootId == p.Id, ctx.Cancel); // system: a bin item is a deleted page; the caller's right to its space is checked next
        if (_root is null) return Problem.NotFound;
        // An Admin restores from the admin bin, without a role in the space. For everyone else an emptied item is gone.
        if (!await access.IsAdminAsync(ctx.Actor, ctx.Cancel))
        {
            if (_root.BinHidden || await access.RoleAsync(ctx.Actor, _root.SpaceId, ctx.Cancel) is null) return Problem.NotFound;
            if (!await access.CanAsync(ctx.Actor, _root.SpaceId, SpaceAction.ManageBinAgingComments, ctx.Cancel)) return Problem.Forbidden;
        }
        return ctx.Actor.UserId is null ? Problem.Unauthorized : null; // the audit entry names who restored it
    }

    public async Task<Outcome<RestoredPageDto>> HandleAsync(ActionContext<RestorePage> ctx)
    {
        var root = _root!;
        var deleted = await db.Pages.IgnoreQueryFilters().Where(p => p.SpaceId == root.SpaceId && p.DeletedAt != null).ToListAsync(ctx.Cancel); // system: what else is in this space's bin decides what comes back and what becomes its own entry
        var byId = deleted.ToDictionary(p => p.Id);
        var oldRoot = deleted.ToDictionary(p => p.Id, p => p.DeleteRootId);
        static void Undelete(Page p) { p.DeletedAt = null; p.DeletedBy = null; p.DeleteRootId = null; p.BinHidden = false; }

        foreach (var p in deleted.Where(p => p.DeleteRootId == root.Id)) Undelete(p);
        // Deleted parent folders come back as empty shells: the folder chain only, never their other contents.
        for (var parent = root.ParentId; parent is { } pid && byId.TryGetValue(pid, out var folder) && folder.DeletedAt is not null; parent = folder.ParentId) Undelete(folder);
        // Whatever stays deleted under a folder that came back becomes its own bin entry.
        foreach (var p in deleted.Where(p => p.DeletedAt is not null && !(p.DeleteRootId is { } r && byId.TryGetValue(r, out var rp) && rp.DeletedAt is not null)))
        {
            var top = p;
            while (top.ParentId is { } up && byId.TryGetValue(up, out var a) && a.DeletedAt is not null && oldRoot[a.Id] == oldRoot[p.Id]) top = a;
            p.DeleteRootId = top.Id;
        }

        if (await db.Pages.AnyAsync(p => p.SpaceId == root.SpaceId && p.ParentId == root.ParentId && p.Kind == root.Kind && p.Title == root.Title)) // system: a live item of the same name in the same place
        {
            const string suffix = " (restored)";
            root.Title = root.Title[..Math.Min(root.Title.Length, 200 - suffix.Length)] + suffix;
            await SearchIndex.StageAsync(db, root, await db.PageTags.Where(t => t.PageId == root.Id).Select(t => t.Tag).ToListAsync(), renderers);
        }
        await db.SaveChangesAsync();
        ctx.Audit(AuditActions.PageRestore, "page", root.Id.ToString(), root.SpaceId, new Dictionary<string, string> { ["title"] = root.Title });
        return new RestoredPageDto(root.Id, root.SpaceId);
    }
}

internal sealed class ListAllBinsHandler(KbDbContext db, IAccess access) : IActionHandler<ListAllBins, IReadOnlyList<BinItemDto>>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ListAllBins> ctx) =>
        await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<IReadOnlyList<BinItemDto>>> HandleAsync(ActionContext<ListAllBins> ctx) =>
        Outcome.List(await BinAsync(db, null, includeHidden: true));
}

internal static class BinChecks
{
    /// <summary>404 without a role in the space, 403 without the right to manage its bin.</summary>
    public static async Task<Problem?> ManagesAsync(IAccess access, Actor actor, Guid spaceId, CancellationToken ct)
    {
        if (await access.RoleAsync(actor, spaceId, ct) is null) return Problem.NotFound;
        return await access.CanAsync(actor, spaceId, SpaceAction.ManageBinAgingComments, ct) ? null : Problem.Forbidden;
    }
}
