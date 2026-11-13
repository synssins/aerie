using Kb.Core.Content;
using Kb.Core.Search;
using Kb.Core.Spaces;
using Kb.Data;
using Microsoft.EntityFrameworkCore;
using ISearchProvider = Kb.Core.Search.ISearchProvider;
using SearchHit = Kb.Core.Search.SearchHit;
using SearchQuery = Kb.Core.Search.SearchQuery;

namespace Kb.Web;

/// <summary>Keeps the search index in step with a page. Staged on the caller's DbContext, saved with the page.</summary>
public static class SearchIndex
{
    public const string SynonymsKey = "Search.Synonyms";

    public static async Task StageAsync(KbDbContext db, Page page, IEnumerable<string> tags, DocRenderers renderers, CancellationToken ct = default)
    {
        var body = page.Kind == PageKind.Page ? DocText.Plain(page.ContentJson, renderers) : "";
        page.SearchText = body;
        var weights = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in SearchText.Tokenize(body)) weights[t] = Math.Min(weights.GetValueOrDefault(t) + 1, 5);
        foreach (var t in tags.SelectMany(SearchText.Tokenize)) weights[t] = weights.GetValueOrDefault(t) + 6;
        foreach (var t in SearchText.Tokenize(page.Title)) weights[t] = weights.GetValueOrDefault(t) + 10;

        var existing = await db.SearchTerms.Where(x => x.PageId == page.Id).ToListAsync(ct);
        foreach (var row in existing)
        {
            if (weights.Remove(row.Term, out var w)) row.Weight = w;
            else db.SearchTerms.Remove(row);
        }
        db.SearchTerms.AddRange(weights.Select(kv => new SearchTerm { PageId = page.Id, Term = kv.Key, Weight = kv.Value }));
    }

    /// <summary>Indexes pages that have no index rows yet (first run after upgrade, or a rebuild).</summary>
    public static async Task<int> BackfillAsync(KbDbContext db, DocRenderers renderers, bool all = false, CancellationToken ct = default)
    {
        var ids = await db.Pages.IgnoreQueryFilters().Where(p => all || !db.SearchTerms.Any(t => t.PageId == p.Id)).Select(p => p.Id).ToListAsync(ct);
        foreach (var chunk in ids.Chunk(100))
        {
            var pages = await db.Pages.IgnoreQueryFilters().Where(p => chunk.Contains(p.Id)).ToListAsync(ct);
            var tags = (await db.PageTags.Where(t => chunk.Contains(t.PageId)).ToListAsync(ct)).ToLookup(t => t.PageId, t => t.Tag);
            foreach (var p in pages) await StageAsync(db, p, tags[p.Id], renderers, ct);
            await db.SaveChangesAsync(ct);
        }
        return ids.Count;
    }
}

/// <summary>Built-in engine: word index in SQL, with prefixes, typo tolerance and synonyms worked out per query.</summary>
public sealed class SqlSearchProvider(KbDbContext db, TimeProvider time) : ISearchProvider
{
    private const int MaxTokens = 8;

    public async Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var tokens = SearchText.Tokenize(query.Text).Distinct().Take(MaxTokens).ToList();
        var tag = string.IsNullOrWhiteSpace(query.Tag) ? null : query.Tag.Trim().ToLowerInvariant();
        var spaces = query.SpaceIds;

        if (tokens.Count == 0)
        {
            if (tag is null) return new SearchResult(0, []);
            var tagged = await db.Pages.AsNoTracking().Where(p => spaces.Contains(p.SpaceId) && db.PageTags.Any(t => t.PageId == p.Id && t.Tag == tag))
                .OrderByDescending(p => p.UpdatedAt).Select(p => p.Id).ToListAsync(ct);
            return new SearchResult(tagged.Count, tagged.Skip(query.Offset).Take(query.Limit).Select(id => new SearchHit(id, 1, [tag])).ToList());
        }

        var synonyms = SearchText.ParseSynonyms(await db.GetAsync(SearchIndex.SynonymsKey, ct) ?? SearchText.DefaultSynonyms);
        // (index word, query token) -> how good a match that word is for that token
        var want = new Dictionary<(string Term, int Token), double>();
        void Put(string term, int token, double w) { if (!want.TryGetValue((term, token), out var old) || old < w) want[(term, token)] = w; }

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var forms = new List<(string Form, double W)> { (token, 1.0) };
            if (synonyms.TryGetValue(token, out var syn)) forms.AddRange(syn.SelectMany(SearchText.Tokenize).Select(s => (s, 0.8)));
            foreach (var (form, w) in forms)
            {
                Put(form, i, w);
                if (form.Length >= 3)
                {
                    var prefixed = await db.SearchTerms.Where(t => t.Term.StartsWith(form)).Select(t => t.Term).Distinct().Take(200).ToListAsync(ct);
                    foreach (var t in prefixed) Put(t, i, w * 0.7);
                }
            }
            var tol = SearchText.Tolerance(token);
            if (tol > 0)
            {
                // Typos: words that start with the same letter and are about as long, then a real edit-distance check.
                var first = token[..1];
                int lo = token.Length - tol, hi = token.Length + tol;
                var near = await db.SearchTerms.Where(t => t.Term.StartsWith(first) && t.Term.Length >= lo && t.Term.Length <= hi)
                    .Select(t => t.Term).Distinct().Take(5000).ToListAsync(ct);
                foreach (var t in near.Where(t => SearchText.Distance(token, t, tol) <= tol)) Put(t, i, 0.5);
            }
        }

        var terms = want.Keys.Select(k => k.Term).Distinct().ToList();
        var rows = await (from st in db.SearchTerms.AsNoTracking()
                          where terms.Contains(st.Term)
                          join p in db.Pages.AsNoTracking() on st.PageId equals p.Id
                          where spaces.Contains(p.SpaceId) && (tag == null || db.PageTags.Any(t => t.PageId == p.Id && t.Tag == tag))
                          select new { st.PageId, st.Term, st.Weight, p.UpdatedAt }).ToListAsync(ct);

        var fresh = time.GetUtcNow().AddDays(-30);
        var ranked = rows.GroupBy(r => r.PageId).Select(g =>
        {
            var best = new double[tokens.Count];
            var matched = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in g)
            {
                for (var i = 0; i < tokens.Count; i++)
                {
                    if (!want.TryGetValue((r.Term, i), out var w)) continue;
                    matched.Add(r.Term);
                    best[i] = Math.Max(best[i], w * (1 + Math.Log(1 + r.Weight)));
                }
            }
            var score = best.Sum() * (g.First().UpdatedAt >= fresh ? 1.1 : 1.0);
            return new { PageId = g.Key, Tokens = best.Count(b => b > 0), Score = score, Matched = matched };
        })
        // A page that has every word beats one that has a few of them many times.
        .OrderByDescending(x => x.Tokens).ThenByDescending(x => x.Score).ToList();

        return new SearchResult(ranked.Count,
            ranked.Skip(query.Offset).Take(query.Limit).Select(x => new SearchHit(x.PageId, Math.Round(x.Score, 3), x.Matched.Order().ToList())).ToList());
    }
}
