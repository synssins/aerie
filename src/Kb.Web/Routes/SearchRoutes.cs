using Kb.Web.Actions;

namespace Kb.Web;

public sealed record SearchAnswerRequest(string? Question, Guid? SpaceId = null);

public static class SearchRoutes
{
    public static void MapSearchEndpoints(this RouteGroupBuilder api)
    {
        // q: words. space: limit to one space. tag: limit to a tag. include=markdown adds each hit's text (maxChars each, default 4000).
        api.MapGet("/search", (string? q, Guid? space, string? tag, int? limit, int? offset, string? include, int? maxChars, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SearchPages { Text = q, SpaceId = space, Tag = tag, Limit = limit, Offset = offset, Include = include, MaxChars = maxChars }, ct).ToHttp()).AllowAnonymous().WithAction<SearchPages>();

        // AI search: an answer from the pages the person may read (contracts/behaviour/ai-search.md). Session only.
        api.MapPost("/search/answer", (SearchAnswerRequest req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new AnswerSearch { Question = req.Question, SpaceId = req.SpaceId }, ct).ToHttp()).WithAction<AnswerSearch>();

        // A page as Markdown: what an agent reads instead of editor JSON.
        api.MapGet("/pages/{id:guid}/markdown", (Guid id, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadPageMarkdown { PageId = id }, ct).ToHttp()).WithAction<ReadPageMarkdown>();

        var admin = api.MapGroup("/admin"); // no filter: each handler answers 403 to a caller who is not an Admin

        admin.MapGet("/search/synonyms", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReadSynonyms(), ct).ToHttp()).WithAction<ReadSynonyms>();

        // The body has the shape of the answer: { text }.
        admin.MapPut("/search/synonyms", (SynonymsDto req, IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new SaveSynonyms { Text = req.Text }, ct).ToHttp()).WithAction<SaveSynonyms>();

        admin.MapPost("/search/reindex", (IActionBus bus, CancellationToken ct) =>
            bus.SendAsync(new ReindexSearch(), ct).ToHttp()).WithAction<ReindexSearch>();
    }
}
