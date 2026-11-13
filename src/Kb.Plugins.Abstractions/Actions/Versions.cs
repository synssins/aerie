namespace Kb.Plugins.Actions;

/// <summary>Every version of a page, newest first.</summary>
[Action("page.version.list", Read = true)]
public sealed record ListVersions : IAction<IReadOnlyList<VersionDto>>
{
    public required Guid PageId { get; init; }
}

/// <summary>The title and content a page had in one version.</summary>
[Action("page.version.read", Read = true)]
public sealed record ReadVersion : IAction<VersionContentDto>
{
    public required Guid PageId { get; init; }
    /// <summary>"major.minor", as in "1.4".</summary>
    public required string Version { get; init; }
}

/// <summary>The lines that differ between two versions of a page.</summary>
[Action("page.diff", Read = true)]
public sealed record DiffPage : IAction<DiffDto>
{
    public required Guid PageId { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
}

/// <summary>Publishes the title and content of an old version as a new version. History is never rewritten.</summary>
[Action("page.version.restore")]
public sealed record RestoreVersion : IAction<PageDto>
{
    public required Guid PageId { get; init; }
    public required string Version { get; init; }
}

/// <summary>The caller's own unsaved work on a page.</summary>
[Action("draft.read", Read = true)]
public sealed record ReadDraft : IAction<DraftDto>
{
    public required Guid PageId { get; init; }
}

/// <summary>Keeps the caller's unsaved work on a page. A page has one draft: someone else's fresh one is not taken over (409).</summary>
[Action("draft.save")]
public sealed record SaveDraft : IAction<DraftSavedDto>
{
    public required Guid PageId { get; init; }
    public string? Title { get; init; }
    public required string ContentJson { get; init; }
    /// <summary>The version the editor loaded.</summary>
    public required string BaseVersion { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
}

/// <summary>Removes the caller's own draft of a page.</summary>
[Action("draft.discard")]
public sealed record DiscardDraft : IAction<Unit>
{
    public required Guid PageId { get; init; }
}

public sealed record DraftDto(string Title, string ContentJson, IReadOnlyList<string> Tags, string BaseVersion, DateTimeOffset UpdatedAt);
public sealed record DraftSavedDto(DateTimeOffset UpdatedAt);
public sealed record VersionContentDto(string Version, string Title, string ContentJson, DateTimeOffset At, string Author);
public sealed record DiffDto(string From, string To, IReadOnlyList<DiffLine> Lines);
