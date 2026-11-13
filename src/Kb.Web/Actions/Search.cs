using Kb.Core.Content;
using Kb.Core.Search;
using Kb.Data;
using Microsoft.EntityFrameworkCore;
using ISearchProvider = Kb.Core.Search.ISearchProvider;
using SearchHit = Kb.Core.Search.SearchHit;
using SearchQuery = Kb.Core.Search.SearchQuery;

namespace Kb.Web.Actions;

internal sealed class SearchPagesHandler(KbDbContext db, IAccess access, ISearchProvider search, DocRenderers renderers) : IActionHandler<SearchPages, SearchResponse>
{
    // Nobody is refused: the answer holds only what is in the spaces the caller may read.
    public Task<Problem?> AuthorizeAsync(ActionContext<SearchPages> ctx) => Task.FromResult<Problem?>(null);

    public async Task<Outcome<SearchResponse>> HandleAsync(ActionContext<SearchPages> ctx)
    {
        var req = ctx.Action;
        var visible = await access.VisibleSpacesAsync(ctx.Actor, ctx.Cancel);
        var spaceIds = req.SpaceId is { } one ? visible.Where(v => v == one).ToList() : visible;
        var take = Math.Clamp(req.Limit ?? 20, 1, 50);
        var skip = Math.Max(req.Offset ?? 0, 0);
        var text = (req.Text ?? "").Trim();
        if (text.Length > 200) text = text[..200];
        var result = await search.SearchAsync(new SearchQuery(text, spaceIds, req.Tag, take, skip), ctx.Cancel);

        var ids = result.Hits.Select(h => h.PageId).ToList();
        var pages = await db.Pages.AsNoTracking().In(new PageScope(spaceIds)).Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ctx.Cancel);
        var hitSpaces = pages.Values.Select(p => p.SpaceId).Distinct().ToList();
        var spaceNames = await db.Spaces.AsNoTracking().Where(s => hitSpaces.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ctx.Cancel);
        var folders = await db.Pages.AsNoTracking().In(new PageScope(hitSpaces)).Where(p => p.Kind == PageKind.Folder)
            .Select(p => new { p.Id, p.ParentId, p.Title }).ToDictionaryAsync(p => p.Id, ctx.Cancel);
        var tags = (await db.PageTags.AsNoTracking().Where(t => ids.Contains(t.PageId)).ToListAsync(ctx.Cancel)).ToLookup(t => t.PageId, t => t.Tag);
        var withText = string.Equals(req.Include, "markdown", StringComparison.OrdinalIgnoreCase);
        var cap = Math.Clamp(req.MaxChars ?? 4000, 200, 20000);

        var hits = new List<SearchHitDto>();
        foreach (var h in result.Hits)
        {
            if (!pages.TryGetValue(h.PageId, out var p)) continue;
            var path = new List<string>();
            var parent = p.ParentId;
            for (var i = 0; parent is { } pid && i < 50 && folders.TryGetValue(pid, out var f); i++) { path.Insert(0, f.Title); parent = f.ParentId; }
            string? md = null;
            if (withText && p.Kind == PageKind.Page) { md = DocText.Markdown(p.ContentJson, renderers); if (md.Length > cap) md = md[..cap]; }
            hits.Add(new SearchHitDto(p.Id, p.SpaceId, spaceNames.GetValueOrDefault(p.SpaceId, ""), p.Kind.ToString(), p.Title, path, $"/p/{p.Id}", p.Version,
                p.UpdatedAt, tags[p.Id].Order().ToList(), SearchText.Snippet(p.SearchText, h.Matched), h.Score, h.Matched, md));
        }
        return new SearchResponse(text, result.Total, take, skip, hits);
    }
}

internal sealed class ReadPageMarkdownHandler(KbDbContext db, IAccess access, DocRenderers renderers) : IActionHandler<ReadPageMarkdown, PageMarkdownDto>
{
    private Page? _page;

    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadPageMarkdown> ctx) =>
        (_page = await db.FindVisibleAsync(access, ctx.Actor, ctx.Action.PageId, track: false, ctx.Cancel)) is null ? Problem.NotFound : null;

    public Task<Outcome<PageMarkdownDto>> HandleAsync(ActionContext<ReadPageMarkdown> ctx)
    {
        var p = _page!;
        return Task.FromResult<Outcome<PageMarkdownDto>>(new PageMarkdownDto(p.Id, p.Title, p.Version, p.UpdatedAt, DocText.Markdown(p.ContentJson, renderers)));
    }
}

// The three actions below are an Admin's: AuthorizeAsync answers 403 to anyone else.

internal sealed class ReadSynonymsHandler(KbDbContext db, IAccess access) : IActionHandler<ReadSynonyms, SynonymsDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ReadSynonyms> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<SynonymsDto>> HandleAsync(ActionContext<ReadSynonyms> ctx) =>
        new SynonymsDto(await db.GetAsync(SearchIndex.SynonymsKey, ctx.Cancel) ?? string.Join('\n', SearchText.DefaultSynonyms.Split('\n').Select(l => l.Trim())));
}

internal sealed class SaveSynonymsHandler(KbDbContext db, IAccess access, TimeProvider time) : IActionHandler<SaveSynonyms, SynonymsDto>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<SaveSynonyms> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    public async Task<Outcome<SynonymsDto>> HandleAsync(ActionContext<SaveSynonyms> ctx)
    {
        var text = ctx.Action.Text;
        if (text is null || text.Length > 20000) return Problem.Invalid("text", "20,000 characters maximum."); // a body without "text" binds null
        await db.SetAsync(SearchIndex.SynonymsKey, text, ctx.Actor.UserId, time.GetUtcNow());
        return new SynonymsDto(text);
    }
}

internal sealed class ReindexSearchHandler(KbDbContext db, IAccess access, DocRenderers renderers) : IActionHandler<ReindexSearch, ReindexResult>
{
    public async Task<Problem?> AuthorizeAsync(ActionContext<ReindexSearch> ctx) => await access.IsAdminAsync(ctx.Actor, ctx.Cancel) ? null : Problem.Forbidden;

    // It writes from its first chunk on, so it takes no cancellation token: a request that goes away leaves no half-built index.
    public async Task<Outcome<ReindexResult>> HandleAsync(ActionContext<ReindexSearch> ctx) => new ReindexResult(await SearchIndex.BackfillAsync(db, renderers, all: true));
}
