namespace Kb.Plugins.Actions;

/// <summary>Finds folders and pages in the spaces the caller may read.</summary>
[Action("search.query", Read = true)]
public sealed record SearchPages : IAction<SearchResponse>
{
    /// <summary>The words to find. Only the first 200 characters count.</summary>
    public string? Text { get; init; }
    /// <summary>Limits the search to one space.</summary>
    public Guid? SpaceId { get; init; }
    public string? Tag { get; init; }
    /// <summary>1 to 50. Null is 20.</summary>
    public int? Limit { get; init; }
    public int? Offset { get; init; }
    /// <summary>"markdown" adds each hit's text.</summary>
    public string? Include { get; init; }
    /// <summary>The cap on each hit's text: 200 to 20000. Null is 4000.</summary>
    public int? MaxChars { get; init; }
}

/// <summary>A page as Markdown: what an agent reads instead of editor JSON.</summary>
[Action("page.markdown", Read = true)]
public sealed record ReadPageMarkdown : IAction<PageMarkdownDto>
{
    public required Guid PageId { get; init; }
}

[Action("search.synonyms.read", Read = true)]
public sealed record ReadSynonyms : IAction<SynonymsDto>;

[Action("search.synonyms.save")]
public sealed record SaveSynonyms : IAction<SynonymsDto>
{
    /// <summary>One group of words that mean the same on each line. 20,000 characters at most.</summary>
    public required string Text { get; init; }
}

/// <summary>Builds the search index again for every page.</summary>
[Action("search.reindex")]
public sealed record ReindexSearch : IAction<ReindexResult>;

/// <summary>
/// One search result. Shaped for people and for agents alike: stable id, link, version to cite, where it lives, and why it matched.
/// <see cref="Text"/> is the page as Markdown, only when asked for with include=markdown.
/// </summary>
public sealed record SearchHitDto(Guid Id, Guid SpaceId, string SpaceName, string Kind, string Title, IReadOnlyList<string> Path, string Url, string Version,
    DateTimeOffset UpdatedAt, IReadOnlyList<string> Tags, string Snippet, double Score, IReadOnlyList<string> Matched, string? Text);
public sealed record SearchResponse(string Query, int Total, int Limit, int Offset, IReadOnlyList<SearchHitDto> Hits);
public sealed record SynonymsDto(string Text);
public sealed record PageMarkdownDto(Guid Id, string Title, string Version, DateTimeOffset UpdatedAt, string Markdown);
/// <summary>How many pages were indexed.</summary>
public sealed record ReindexResult(int Pages);
