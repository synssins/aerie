using System.Text.Json;
using Kb.Core.Ai;
using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Core.Search;
using Kb.Data;
using ISearchProvider = Kb.Core.Search.ISearchProvider;
using SearchQuery = Kb.Core.Search.SearchQuery;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.Actions;

public sealed record SearchAnswerSourceDto(int N, Guid PageId, string Title, Guid SpaceId, string SpaceName, string Url);

/// <summary>The answer to a question (contracts/behaviour/ai-search.md, The answer; question Q-0031). Plain text.</summary>
public sealed record SearchAnswerDto(string Outcome, string Answer, IReadOnlyList<SearchAnswerSourceDto> Sources);

/// <summary>Asks the configured model a question about the pages the caller may read.</summary>
[Action("search.answer")]
public sealed record AnswerSearch : IAction<SearchAnswerDto>
{
    public string? Question { get; init; }
    public Guid? SpaceId { get; init; }
}

/// <summary>What AI search shares: availability, the fixed texts, the material and the audit record.</summary>
internal static class AiSearch
{
    public const int MinQuestion = 3, MaxQuestion = 500, MaxPages = 8, RecordedQuestion = 2000;

    /// <summary>aerie's own words, never the model's. Neither says, or hints, whether pages exist that the person cannot read.</summary>
    public const string NoAnswerText = "The pages you can read do not seem to answer this question. Try an ordinary search.";
    public const string NothingFoundText = "No pages you can read match this question.";

    /// <summary>Available to the person: AI on, a model configured, and the search feature open to them (ai-search.md, When it is available).</summary>
    public static async Task<bool> AvailableAsync(AiService ai, Actor actor, CancellationToken ct)
    {
        if (actor.UserId is null || actor.TokenId is not null) return false;
        var s = await ai.SettingsAsync(ct);
        return s.Enabled && !string.IsNullOrWhiteSpace(s.BaseUrl) && !string.IsNullOrWhiteSpace(s.Model) && await ai.MayUseAsync(actor, AiFeatures.Search, s, ct);
    }

    /// <summary>One page given to the model: numbered from 1, with its text cut to its share.</summary>
    public sealed record Material(int N, Page Page, string SpaceName, string Text);

    /// <summary>
    /// The pages the model may see, in this order (ai-search.md, What the model may see; question Q-0030): aerie's search for the question as the person,
    /// without sensitive spaces, pages only, each read checked again rather than trusting the index, pages flagged for text addressed to an AI left out
    /// as for every AI feature, the first 8. Each text gets an equal share of what
    /// maxInputChars leaves after the question, cut at the end.
    /// </summary>
    public static async Task<IReadOnlyList<Material>> MaterialAsync(KbDbContext db, IAccess access, ISearchProvider search, Actor actor, string question, Guid? spaceId,
        int maxInputChars, CancellationToken ct)
    {
        var visible = await access.VisibleSpacesAsync(actor, ct);
        var spaces = await db.Spaces.AsNoTracking().Where(s => visible.Contains(s.Id) && !s.IsSensitive && (spaceId == null || s.Id == spaceId))
            .ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        if (spaces.Count == 0) return [];
        var text = question.Length > 200 ? question[..200] : question;
        var hits = await search.SearchAsync(new SearchQuery(text, [.. spaces.Keys], null, 50, 0), ct);
        var pages = new List<(Page Page, string Space)>();
        foreach (var h in hits.Hits)
        {
            if (pages.Count == MaxPages) break;
            if (pages.Any(p => p.Page.Id == h.PageId)) continue;
            if (await db.FindVisibleAsync(access, actor, h.PageId, track: false, ct) is not { Kind: PageKind.Page } page) continue;
            if (!spaces.TryGetValue(page.SpaceId, out var name)) continue; // sensitive, or outside the filter
            if (page.AiFlag is not null) continue; // aerie's guardrail: a page holding text addressed to an AI is never sent (PageScan.ForAi)
            pages.Add((page, name));
        }
        if (pages.Count == 0) return [];
        var share = Math.Max(0, (maxInputChars - question.Length) / pages.Count);
        return [.. pages.Select((p, i) => new Material(i + 1, p.Page, p.Space, Cut($"{p.Page.Title}\n{p.Space}\n{p.Page.SearchText}", share)))];
    }

    private static string Cut(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>
    /// The aiSearch record of the call's audit entry (question Q-0032, revised): the question as sent, cut to its first 2000 characters, its length,
    /// the outcome, the space filter and the pages given to the model with the version sent. No page text and no answer text.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Record(string? question, string outcome, Guid? spaceId, IReadOnlyList<Material> pages)
    {
        var q = question ?? "";
        var record = new AuditAiSearchDto(q.Length > RecordedQuestion ? q[..RecordedQuestion] : q, q.Length, outcome, spaceId,
            [.. pages.Select(p => new AuditAiSearchPageDto(p.Page.Id, p.Page.Version))]);
        return new Dictionary<string, string> { [AuditAiSearch.Key] = JsonSerializer.Serialize(record, AuditAiSearch.Json) };
    }
}

/// <summary>How the aiSearch record is kept in an audit entry's data, and read back.</summary>
public static class AuditAiSearch
{
    public const string Key = "aiSearch";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static AuditAiSearchDto? Read(IReadOnlyDictionary<string, string> data) =>
        data.TryGetValue(Key, out var json) ? JsonSerializer.Deserialize<AuditAiSearchDto>(json, Json) : null;
}

internal sealed class AnswerSearchHandler(KbDbContext db, IAccess access, AiService ai, AiRateLimiter limiter, ISearchProvider search)
    : IActionHandler<AnswerSearch, SearchAnswerDto>
{
    private AiSettings _settings = null!;

    // Guests get 401 and are not recorded; API tokens are refused before this (session only, question Q-0039). Every call with a session is recorded.
    public async Task<Problem?> AuthorizeAsync(ActionContext<AnswerSearch> ctx)
    {
        if (ctx.Actor.UserId is null) return Problem.Unauthorized;
        _settings = await ai.SettingsAsync(ctx.Cancel);
        if (await AiSearch.AvailableAsync(ai, ctx.Actor, ctx.Cancel)) return null;
        Record(ctx, "refused");
        return new Problem(403, "AI search is not open to you.");
    }

    private static void Record(ActionContext<AnswerSearch> ctx, string outcome, IReadOnlyList<AiSearch.Material>? pages = null) =>
        ctx.Audit(AuditActions.SearchAnswer, data: AiSearch.Record(ctx.Action.Question, outcome, ctx.Action.SpaceId, pages ?? []), evenIfRefused: true);

    public async Task<Outcome<SearchAnswerDto>> HandleAsync(ActionContext<AnswerSearch> ctx)
    {
        var (a, user) = (ctx.Action, ctx.Actor.UserId!.Value);
        Problem Refuse(Problem p, string outcome = "refused") { Record(ctx, outcome); return p; }

        var question = a.Question?.Trim() ?? "";
        if (question.Length is < AiSearch.MinQuestion or > AiSearch.MaxQuestion) return Refuse(Problem.Invalid("question", $"{AiSearch.MinQuestion} to {AiSearch.MaxQuestion} characters."));
        if (a.SpaceId is { } spaceId)
        {
            if (await access.RoleAsync(ctx.Actor, spaceId, ctx.Cancel) is null) return Refuse(Problem.NotFound);
            if (await db.Spaces.AnyAsync(s => s.Id == spaceId && s.IsSensitive, ctx.Cancel)) return Refuse(Problem.Refused("Sensitive spaces are never sent to the AI."));
        }
        // The per-person AI limit, shared with aerie's other AI features (question Q-0032); checked before any work, so nothing_found counts too.
        if (!limiter.Allow($"u:{user}", _settings.UserPerMinute)) return Refuse(Problem.TooMany("Too many AI calls. Wait a minute.", 60), "rate_limited");

        var material = await AiSearch.MaterialAsync(db, access, search, ctx.Actor, question, a.SpaceId, _settings.MaxInputChars, ctx.Cancel);
        if (material.Count == 0)
        {
            Record(ctx, "nothing_found");
            return new SearchAnswerDto("nothing_found", AiSearch.NothingFoundText, []);
        }
        // shortcut: slice A1 stops here; the model call comes with slice A2 (ai-search.md, Build slices).
        return Refuse(new Problem(503, "AI search cannot answer yet."), "failed");
    }
}
