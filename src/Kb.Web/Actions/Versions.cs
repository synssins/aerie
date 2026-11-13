using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Data;
using Microsoft.EntityFrameworkCore;
using static Kb.Web.PageSupport;

namespace Kb.Web.Actions;

internal sealed class ListVersionsHandler(KbDbContext db, IAccess access) : IActionHandler<ListVersions, IReadOnlyList<VersionDto>>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ListVersions> ctx) =>
        await db.FindVisibleAsync(access, ctx.Actor, ctx.Action.PageId, track: false, ctx.Cancel) is null ? Problem.NotFound : null;

    public async Task<Outcome<IReadOnlyList<VersionDto>>> HandleAsync(ActionContext<ListVersions> ctx) =>
        Outcome.List(await HistoryAsync(db, ctx.Action.PageId, int.MaxValue));
}

internal sealed class ReadVersionHandler(KbDbContext db, IAccess access) : IActionHandler<ReadVersion, VersionContentDto>
{
    private PageVersion? _version;

    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadVersion> ctx)
    {
        if (await db.FindVisibleAsync(access, ctx.Actor, ctx.Action.PageId, track: false, ctx.Cancel) is null) return Problem.NotFound;
        return (_version = await VersionAsync(db, ctx.Action.PageId, ctx.Action.Version)) is null ? Problem.NotFound : null;
    }

    public async Task<Outcome<VersionContentDto>> HandleAsync(ActionContext<ReadVersion> ctx)
    {
        var v = _version!;
        var author = await db.Users.Where(u => u.Id == v.AuthorId).Select(u => u.DisplayName).FirstOrDefaultAsync(ctx.Cancel) ?? "Unknown";
        return new VersionContentDto(ctx.Action.Version, v.Title, v.ContentJson, v.At, author);
    }
}

internal sealed class DiffPageHandler(KbDbContext db, IAccess access) : IActionHandler<DiffPage, DiffDto>
{
    private PageVersion? _from, _to;

    public async Task<Problem?> AuthorizeAsync(ActionContext<DiffPage> ctx)
    {
        var req = ctx.Action;
        if (await db.FindVisibleAsync(access, ctx.Actor, req.PageId, track: false, ctx.Cancel) is null) return Problem.NotFound;
        return (_from = await VersionAsync(db, req.PageId, req.From)) is null || (_to = await VersionAsync(db, req.PageId, req.To)) is null ? Problem.NotFound : null;
    }

    public Task<Outcome<DiffDto>> HandleAsync(ActionContext<DiffPage> ctx)
    {
        static List<string> Lines(PageVersion v) => [$"# {v.Title}", "", .. DocText.MarkdownLines(v.ContentJson)];
        return Task.FromResult<Outcome<DiffDto>>(new DiffDto(ctx.Action.From, ctx.Action.To, LineDiff.Diff(Lines(_from!), Lines(_to!))));
    }
}

// Restoring never rewrites history: the old content becomes a new version. It publishes through PageSupport, not by sending page.save.
internal sealed class RestoreVersionHandler(KbDbContext db, IAccess access, TimeProvider time) : IActionHandler<RestoreVersion, PageDto>
{
    private Page? _page;
    private PageVersion? _version;

    public async Task<Problem?> AuthorizeAsync(ActionContext<RestoreVersion> ctx)
    {
        if ((_page = await db.FindVisibleAsync(access, ctx.Actor, ctx.Action.PageId, track: true, ctx.Cancel)) is null) return Problem.NotFound;
        if (!await access.CanAsync(ctx.Actor, _page.SpaceId, SpaceAction.CreateEdit, ctx.Cancel)) return Problem.Forbidden;
        if ((_version = await VersionAsync(db, _page.Id, ctx.Action.Version)) is null) return Problem.NotFound;
        return ctx.Actor.UserId is null ? Problem.Unauthorized : null; // a version names its author
    }

    public async Task<Outcome<PageDto>> HandleAsync(ActionContext<RestoreVersion> ctx)
    {
        var (page, v, version) = (_page!, _version!, ctx.Action.Version);
        page.Title = v.Title;
        var tags = await db.PageTags.Where(t => t.PageId == page.Id).Select(t => t.Tag).ToListAsync(ctx.Cancel);
        var result = await PublishAsync(db, access, ctx.Actor, page, v.ContentJson, major: false, $"Restored from {version}", tags, time.GetUtcNow());
        // As before, the entry is asked for whatever the publish answered: evenIfRefused keeps it when the publish answers a conflict.
        ctx.Audit(AuditActions.PageVersionRestore, "page", page.Id.ToString(), page.SpaceId, new Dictionary<string, string> { ["from"] = version, ["to"] = page.Version }, evenIfRefused: true);
        return result;
    }
}

// A draft belongs to its author. Reading and discarding one ask for no role in the space, as before: every query below is narrowed to the caller.

internal sealed class ReadDraftHandler(KbDbContext db) : IActionHandler<ReadDraft, DraftDto>
{
    private PageDraft? _draft;

    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadDraft> ctx)
    {
        if (ctx.Actor.UserId is not { } me) return Problem.Unauthorized;
        var id = ctx.Action.PageId;
        return (_draft = await db.PageDrafts.AsNoTracking().FirstOrDefaultAsync(x => x.PageId == id && x.UserId == me, ctx.Cancel)) is null ? Problem.NotFound : null;
    }

    public Task<Outcome<DraftDto>> HandleAsync(ActionContext<ReadDraft> ctx)
    {
        var d = _draft!;
        return Task.FromResult<Outcome<DraftDto>>(new DraftDto(d.Title, d.ContentJson, d.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries), d.BaseVersion, d.UpdatedAt));
    }
}

internal sealed class SaveDraftHandler(KbDbContext db, IAccess access, TimeProvider time) : IActionHandler<SaveDraft, DraftSavedDto>
{
    private static readonly TimeSpan DraftLock = TimeSpan.FromHours(24);

    public async Task<Problem?> AuthorizeAsync(ActionContext<SaveDraft> ctx)
    {
        // Only a page has a draft: a folder answers like a page that is not there.
        if (await db.FindVisibleAsync(access, ctx.Actor, ctx.Action.PageId, track: false, ctx.Cancel) is not { Kind: PageKind.Page } page) return Problem.NotFound;
        if (!await access.CanAsync(ctx.Actor, page.SpaceId, SpaceAction.CreateEdit, ctx.Cancel)) return Problem.Forbidden;
        return ctx.Actor.UserId is null ? Problem.Unauthorized : null; // a draft names its author
    }

    public async Task<Outcome<DraftSavedDto>> HandleAsync(ActionContext<SaveDraft> ctx)
    {
        var (req, me) = (ctx.Action, ctx.Actor.UserId!.Value);
        if (CleanTags(req.Tags) is not { } tags) return TagError();
        var title = (req.Title ?? "").Trim();
        if (title.Length > 200 || !IsEditorDoc(req.ContentJson) || req.BaseVersion is not { Length: > 0 and <= 24 }) return Problem.Invalid("draft", "Invalid draft.");

        var now = time.GetUtcNow();
        var d = await db.PageDrafts.FirstOrDefaultAsync(x => x.PageId == req.PageId, ctx.Cancel);
        // One draft per page. Someone else's stays theirs unless it has sat untouched for a day.
        if (d is not null && d.UserId != me && d.UpdatedAt > now - DraftLock) return new Problem(409);
        if (d is null) db.PageDrafts.Add(d = new PageDraft { PageId = req.PageId, Title = title, ContentJson = req.ContentJson, BaseVersion = req.BaseVersion });
        d.UserId = me; d.Title = title; d.ContentJson = req.ContentJson; d.BaseVersion = req.BaseVersion; d.Tags = string.Join(',', tags); d.UpdatedAt = now;
        try { await db.SaveChangesAsync(); } catch (DbUpdateException) { return new Problem(409); }
        await ImageEndpoints.KeepAsync(db, req.ContentJson);
        return new DraftSavedDto(now);
    }
}

internal sealed class DiscardDraftHandler(KbDbContext db) : IActionHandler<DiscardDraft, Unit>
{
    public Task<Problem?> AuthorizeAsync(ActionContext<DiscardDraft> ctx) => Task.FromResult(ctx.Actor.UserId is null ? Problem.Unauthorized : null);

    public async Task<Outcome<Unit>> HandleAsync(ActionContext<DiscardDraft> ctx)
    {
        var (id, me) = (ctx.Action.PageId, ctx.Actor.UserId!.Value);
        await db.PageDrafts.Where(x => x.PageId == id && x.UserId == me).ExecuteDeleteAsync();
        return new Unit();
    }
}
