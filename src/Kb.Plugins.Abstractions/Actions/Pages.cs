namespace Kb.Plugins.Actions;

/// <summary>The folders and pages of a space, flat; each names its parent.</summary>
[Action("tree.read", Read = true)]
public sealed record ReadTree : IAction<IReadOnlyList<TreeNodeDto>>
{
    public required Guid SpaceId { get; init; }
}

[Action("page.create")]
public sealed record CreatePage : IAction<PageDto>
{
    public required Guid SpaceId { get; init; }
    public Guid? ParentId { get; init; }
    /// <summary>"Folder" or "Page".</summary>
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public string? ContentJson { get; init; }
}

[Action("page.read", Read = true)]
public sealed record ReadPage : IAction<PageDto>
{
    public required Guid PageId { get; init; }
}

/// <summary>Saves a page as a new version, or renames a folder.</summary>
[Action("page.save")]
public sealed record SavePage : IAction<PageDto>
{
    public required Guid PageId { get; init; }
    public required string Title { get; init; }
    public required string ContentJson { get; init; }
    /// <summary>The version the editor loaded. A save against an older one is refused with 409.</summary>
    public required string BaseVersion { get; init; }
    public bool Major { get; init; }
    public string? Note { get; init; }
    /// <summary>Null leaves the tags as they are.</summary>
    public IReadOnlyList<string>? Tags { get; init; }
}

public sealed record TreeNodeDto(Guid Id, Guid? ParentId, string Kind, string Title, int Sort);
public sealed record CrumbDto(Guid Id, string Title);
public sealed record VersionDto(string Version, DateTimeOffset At, string Author, string? Note);
public sealed record DraftInfoDto(bool Mine, string By, DateTimeOffset UpdatedAt);
public sealed record AttachmentDto(Guid Id, string Name, long Size, string Url, DateTimeOffset At);
public sealed record PageDto(Guid Id, Guid SpaceId, Guid? ParentId, string Kind, string Title, string ContentJson, string Version,
    DateTimeOffset UpdatedAt, string UpdatedBy, IReadOnlyList<CrumbDto> Path, IReadOnlyList<VersionDto> History, bool CanEdit, IReadOnlyList<string> Tags,
    bool CanDelete, DraftInfoDto? Draft, IReadOnlyList<AttachmentDto> Attachments, long MaxAttachmentBytes);
