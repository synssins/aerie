using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Data;
using Microsoft.EntityFrameworkCore;
using static Kb.Web.PageSupport;

namespace Kb.Web.Actions;

// The pattern every handler follows. AuthorizeAsync answers 404, 403 or 401 and may keep what it loaded: the handler lives for one attempt.
// HandleAsync validates and does the work. Reads take ctx.Cancel; from the first write on nothing does, so a request that goes away
// cannot stop an action half way.

internal sealed class ReadTreeHandler(KbDbContext db, IAccess access) : IActionHandler<ReadTree, IReadOnlyList<TreeNodeDto>>
{
    private PageScope _scope = new([]);

    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadTree> ctx)
    {
        _scope = await PageScopes.ForAsync(access, ctx.Actor, [ctx.Action.SpaceId], ctx.Cancel);
        return _scope.SpaceIds.Count == 0 ? Problem.NotFound : null;
    }

    public async Task<Outcome<IReadOnlyList<TreeNodeDto>>> HandleAsync(ActionContext<ReadTree> ctx) =>
        Outcome.List(await db.Pages.AsNoTracking().In(_scope).OrderBy(p => p.Sort)
            .Select(p => new TreeNodeDto(p.Id, p.ParentId, p.Kind.ToString(), p.Title, p.Sort)).ToListAsync(ctx.Cancel));
}

internal sealed class CreatePageHandler(KbDbContext db, IAccess access, TimeProvider time, DocRenderers renderers) : IActionHandler<CreatePage, PageDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<CreatePage> ctx)
    {
        if (await access.RoleAsync(ctx.Actor, ctx.Action.SpaceId, ctx.Cancel) is null) return Problem.NotFound;
        if (!await access.CanAsync(ctx.Actor, ctx.Action.SpaceId, SpaceAction.CreateEdit, ctx.Cancel)) return Problem.Forbidden;
        return ctx.Actor.UserId is null ? Problem.Unauthorized : null; // a page names who made it
    }

    public async Task<Outcome<PageDto>> HandleAsync(ActionContext<CreatePage> ctx)
    {
        var (req, me) = (ctx.Action, ctx.Actor.UserId!.Value);
        if (!Enum.TryParse<PageKind>(req.Kind, out var kind) || !Enum.IsDefined(kind)) return Problem.Invalid("kind", "Folder or Page.");
        if (TitleError(req.Title) is { } title) return title;
        if (CleanTags(req.Tags) is not { } tags) return TagError();
        if (kind == PageKind.Page && req.ContentJson is not null && !IsEditorDoc(req.ContentJson)) return Problem.Invalid("contentJson", "Not an editor document.");
        if (req.ParentId is { } parent && !await db.Pages.AnyAsync(p => p.Id == parent && p.SpaceId == req.SpaceId && p.Kind == PageKind.Folder, ctx.Cancel)) // system: the parent must be a folder of this space
            return Problem.Invalid("parentId", "Must be a folder in this space.");

        var now = time.GetUtcNow();
        var page = new Page { SpaceId = req.SpaceId, ParentId = req.ParentId, Kind = kind, Title = req.Title.Trim(), Sort = await NextSortAsync(db, req.SpaceId, req.ParentId), CreatedBy = me, CreatedAt = now, UpdatedBy = me, UpdatedAt = now };
        if (kind == PageKind.Page) page.ContentJson = DocLinks.Clean(req.ContentJson ?? """{"type":"doc","content":[]}""");
        var flagged = PageScan.Apply(page);
        db.Pages.Add(page);
        db.PageTags.AddRange(tags.Select(t => new PageTag { PageId = page.Id, Tag = t }));
        if (kind == PageKind.Page)
            db.PageVersions.Add(new PageVersion { PageId = page.Id, Major = 1, Minor = 0, Title = page.Title, ContentJson = page.ContentJson, AuthorId = me, At = now });
        await SearchIndex.StageAsync(db, page, tags, renderers);
        await db.SaveChangesAsync();
        await ImageEndpoints.KeepAsync(db, page.ContentJson, page.SpaceId);
        if (flagged) AuditAiFlag(ctx, page);
        return await ToDtoAsync(db, access, ctx.Actor, page);
    }
}

internal sealed class ReadPageHandler(KbDbContext db, IAccess access) : IActionHandler<ReadPage, PageDto>
{
    private Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadPage> ctx) =>
        (_page = await db.FindVisibleAsync(access, ctx.Actor, ctx.Action.PageId, track: false, ctx.Cancel)) is null ? Problem.NotFound : null;

    public async Task<Outcome<PageDto>> HandleAsync(ActionContext<ReadPage> ctx) => await ToDtoAsync(db, access, ctx.Actor, _page!);
}

internal sealed class SavePageHandler(KbDbContext db, IAccess access, TimeProvider time, DocRenderers renderers) : IActionHandler<SavePage, PageDto>
{
    private Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<SavePage> ctx)
    {
        if ((_page = await db.FindVisibleAsync(access, ctx.Actor, ctx.Action.PageId, track: true, ctx.Cancel)) is null) return Problem.NotFound;
        if (!await access.CanAsync(ctx.Actor, _page.SpaceId, SpaceAction.CreateEdit, ctx.Cancel)) return Problem.Forbidden;
        return ctx.Actor.UserId is null ? Problem.Unauthorized : null;
    }

    public async Task<Outcome<PageDto>> HandleAsync(ActionContext<SavePage> ctx)
    {
        var (req, page) = (ctx.Action, _page!);
        if (TitleError(req.Title) is { } title) return title;
        if (CleanTags(req.Tags) is not { } tags) return TagError();
        if (page.Kind == PageKind.Page && !IsEditorDoc(req.ContentJson)) return Problem.Invalid("contentJson", "Not an editor document.");
        if (req.Note is { Length: > MaxNote }) return Problem.Invalid("note", $"{MaxNote} characters at most.");
        // Someone else saved since this editor loaded: refuse rather than overwrite.
        if (req.BaseVersion != page.Version) return Problem.Conflict(body: new { current = page.Version });

        page.Title = req.Title.Trim();
        if (req.Tags is not null)
        {
            db.PageTags.RemoveRange(db.PageTags.Where(t => t.PageId == page.Id));
            db.PageTags.AddRange(tags.Select(t => new PageTag { PageId = page.Id, Tag = t }));
        }
        else tags = await db.PageTags.Where(t => t.PageId == page.Id).Select(t => t.Tag).ToListAsync(ctx.Cancel);
        var flagBefore = page.AiFlag;
        var saved = await PublishAsync(db, access, renderers, ctx.Actor, page, req.ContentJson, req.Major, req.Note, tags, time.GetUtcNow());
        if (saved.Problem is null && page.AiFlag != flagBefore) AuditAiFlag(ctx, page);
        return saved;
    }
}

/// <summary>
/// An Owner of the page's space, or an Admin, marks a flagged page reviewed: its instruction-like text is meant (a page about
/// prompt injection). It stays clear until other such text is found. Core only.
/// </summary>
[Action("page.ai.review")]
public sealed record ReviewPageAi : IAction<PageDto>
{
    public required Guid PageId { get; init; }
}

internal sealed class ReviewPageAiHandler(KbDbContext db, IAccess access) : IActionHandler<ReviewPageAi, PageDto>
{
    private Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<ReviewPageAi> ctx)
    {
        if ((_page = await db.FindVisibleAsync(access, ctx.Actor, ctx.Action.PageId, track: true, ctx.Cancel)) is null) return Problem.NotFound;
        if (ctx.Actor.TokenId is not null) return Problem.Forbidden;
        return await access.IsAdminAsync(ctx.Actor, ctx.Cancel) || await access.RoleAsync(ctx.Actor, _page.SpaceId, ctx.Cancel) == SpaceRole.Owner
            ? null : new Problem(403, "Only an Owner of the space or an Admin marks a page reviewed.");
    }

    public async Task<Outcome<PageDto>> HandleAsync(ActionContext<ReviewPageAi> ctx)
    {
        var page = _page!;
        var findings = PageScan.Scan(page.ContentJson);
        var found = page.AiFlag;
        page.AiReviewed = findings.Count == 0 ? null : PageScan.Hash(findings);
        page.AiFlag = null;
        await db.SaveChangesAsync(ctx.Cancel);
        ctx.Audit(AuditActions.PageAiReview, "page", page.Id.ToString(), page.SpaceId, new Dictionary<string, string> { ["found"] = found ?? PageScan.Describe(findings) });
        return await PageSupport.ToDtoAsync(db, access, ctx.Actor, page);
    }
}
