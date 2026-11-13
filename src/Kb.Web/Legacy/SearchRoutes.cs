using Kb.Core.Content;
using Kb.Core.Search;
using Kb.Core.Spaces;
using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web;

/// <summary>
/// One search result. Shaped for people and for agents alike: stable id, link, version to cite, where it lives, and why it matched.
/// <see cref="Text"/> is the page as Markdown, only when asked for with include=markdown.
/// </summary>
public sealed record SearchHitDto(Guid Id, Guid SpaceId, string SpaceName, string Kind, string Title, IReadOnlyList<string> Path, string Url, string Version,
    DateTimeOffset UpdatedAt, IReadOnlyList<string> Tags, string Snippet, double Score, IReadOnlyList<string> Matched, string? Text);
public sealed record SearchResponse(string Query, int Total, int Limit, int Offset, IReadOnlyList<SearchHitDto> Hits);
public sealed record SynonymsDto(string Text);
public sealed record PageMarkdownDto(Guid Id, string Title, string Version, DateTimeOffset UpdatedAt, string Markdown);

public static class SearchEndpoints
{
    public static void MapSearchEndpoints(this RouteGroupBuilder api)
    {
        // q: words. space: limit to one space. tag: limit to a tag. include=markdown adds each hit's text (maxChars each, default 4000).
        api.MapGet("/search", async (string? q, Guid? space, string? tag, int? limit, int? offset, string? include, int? maxChars,
            HttpContext http, ISpacePermissionService perms, ISearchProvider search, KbDbContext db, CancellationToken ct) =>
        {
            var visible = await perms.VisibleSpaceIdsAsync(http.User.UserId(), ct);
            var spaceIds = space is { } one ? visible.Where(v => v == one).ToList() : visible;
            var take = Math.Clamp(limit ?? 20, 1, 50);
            var skip = Math.Max(offset ?? 0, 0);
            var text = (q ?? "").Trim();
            if (text.Length > 200) text = text[..200];
            var result = await search.SearchAsync(new SearchQuery(text, spaceIds, tag, take, skip), ct);

            var ids = result.Hits.Select(h => h.PageId).ToList();
            var pages = await db.Pages.AsNoTracking().Where(p => ids.Contains(p.Id) && spaceIds.Contains(p.SpaceId)).ToDictionaryAsync(p => p.Id, ct);
            var hitSpaces = pages.Values.Select(p => p.SpaceId).Distinct().ToList();
            var spaceNames = await db.Spaces.AsNoTracking().Where(s => hitSpaces.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
            var folders = await db.Pages.AsNoTracking().Where(p => hitSpaces.Contains(p.SpaceId) && p.Kind == PageKind.Folder)
                .Select(p => new { p.Id, p.ParentId, p.Title }).ToDictionaryAsync(p => p.Id, ct);
            var tags = (await db.PageTags.AsNoTracking().Where(t => ids.Contains(t.PageId)).ToListAsync(ct)).ToLookup(t => t.PageId, t => t.Tag);
            var withText = string.Equals(include, "markdown", StringComparison.OrdinalIgnoreCase);
            var cap = Math.Clamp(maxChars ?? 4000, 200, 20000);

            var hits = new List<SearchHitDto>();
            foreach (var h in result.Hits)
            {
                if (!pages.TryGetValue(h.PageId, out var p)) continue;
                var path = new List<string>();
                var parent = p.ParentId;
                for (var i = 0; parent is { } pid && i < 50 && folders.TryGetValue(pid, out var f); i++) { path.Insert(0, f.Title); parent = f.ParentId; }
                string? md = null;
                if (withText && p.Kind == PageKind.Page) { md = DocText.Markdown(p.ContentJson); if (md.Length > cap) md = md[..cap]; }
                hits.Add(new SearchHitDto(p.Id, p.SpaceId, spaceNames.GetValueOrDefault(p.SpaceId, ""), p.Kind.ToString(), p.Title, path, $"/p/{p.Id}", p.Version,
                    p.UpdatedAt, tags[p.Id].Order().ToList(), SearchText.Snippet(p.SearchText, h.Matched), h.Score, h.Matched, md));
            }
            return Results.Ok(new SearchResponse(text, result.Total, take, skip, hits));
        });

        // A page as Markdown: what an agent reads instead of editor JSON.
        api.MapGet("/pages/{id:guid}/markdown", async (Guid id, HttpContext http, ISpacePermissionService perms, KbDbContext db, CancellationToken ct) =>
        {
            var p = await db.Pages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (p is null || await perms.GetRoleAsync(http.User.UserId(), p.SpaceId, ct) is null) return Results.NotFound();
            return Results.Ok(new PageMarkdownDto(p.Id, p.Title, p.Version, p.UpdatedAt, DocText.Markdown(p.ContentJson)));
        });

        var admin = api.AdminGroup();
        admin.MapGet("/search/synonyms", async (KbDbContext db, CancellationToken ct) =>
            Results.Ok(new SynonymsDto(await db.GetAsync(SearchIndex.SynonymsKey, ct) ?? string.Join('\n', SearchText.DefaultSynonyms.Split('\n').Select(l => l.Trim())))));
        admin.MapPut("/search/synonyms", async (SynonymsDto req, HttpContext http, KbDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            if (req.Text is null || req.Text.Length > 20000) return Results.ValidationProblem(new Dictionary<string, string[]> { ["text"] = ["20,000 characters maximum."] });
            await db.SetAsync(SearchIndex.SynonymsKey, req.Text, http.User.UserId(), time.GetUtcNow(), ct);
            return Results.Ok(new SynonymsDto(req.Text));
        });
        admin.MapPost("/search/reindex", async (KbDbContext db, CancellationToken ct) => Results.Ok(new { pages = await SearchIndex.BackfillAsync(db, all: true, ct) }));
    }
}
