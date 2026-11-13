namespace Kb.Plugins.Actions;

/// <summary>Moves a page, or a folder with everything under it, to another folder or another space.</summary>
[Action("page.move")]
public sealed record MovePage : IAction<PageDto>
{
    public required Guid PageId { get; init; }
    /// <summary>Null keeps the space.</summary>
    public Guid? SpaceId { get; init; }
    /// <summary>Null is the top of the space.</summary>
    public Guid? ParentId { get; init; }
}

/// <summary>Moves a page, or a folder with everything under it, to the recycle bin. Nothing is destroyed.</summary>
[Action("page.delete")]
public sealed record DeletePage : IAction<Unit>
{
    public required Guid PageId { get; init; }
}

/// <summary>The recycle bin of a space, without what was emptied from it.</summary>
[Action("bin.list", Read = true)]
public sealed record ListBin : IAction<IReadOnlyList<BinItemDto>>
{
    public required Guid SpaceId { get; init; }
}

/// <summary>Hides the items from the space's bin. The admin bin keeps them.</summary>
[Action("bin.empty")]
public sealed record EmptyBin : IAction<Unit>
{
    public required Guid SpaceId { get; init; }
}

/// <summary>Brings a bin item back in place. An Admin restores from the admin bin without a role in the space.</summary>
[Action("page.restore")]
public sealed record RestorePage : IAction<RestoredPageDto>
{
    public required Guid PageId { get; init; }
}

/// <summary>The recycle bins of every space, with what was emptied from them. An Admin's.</summary>
[Action("bin.list.all", Read = true)]
public sealed record ListAllBins : IAction<IReadOnlyList<BinItemDto>>;

/// <summary>The recycle bin of a space as a tree: every deleted item, each under its deleted folder. Without what was emptied.</summary>
[Action("bin.browse", Read = true)]
public sealed record BrowseBin : IAction<IReadOnlyList<BinNodeDto>>
{
    public required Guid SpaceId { get; init; }
}

/// <summary>
/// The admin bin as a tree: the bins of every space the Admin manages, with what was emptied, and every deleted space with all
/// that is in it. A deleted sensitive space is left out: its content is nobody's to read while it is deleted.
/// </summary>
[Action("bin.browse.all", Read = true)]
public sealed record BrowseAllBins : IAction<IReadOnlyList<BinNodeDto>>;

/// <summary>One item of a recycle bin, to read. For whoever manages that bin; in a deleted space, for an Admin.</summary>
[Action("bin.page.read", Read = true)]
public sealed record ReadBinPage : IAction<BinPageDto>
{
    public required Guid PageId { get; init; }
}

public sealed record BinNodeDto(Guid Id, Guid? ParentId, Guid SpaceId, string Kind, string Title, int Sort);
/// <param name="Path">The space's name, then the folders down to the item.</param>
/// <param name="Children">What a folder holds in this bin, to open next.</param>
/// <param name="RestoreId">The bin item that brings this one back (<c>page.restore</c>). Null in a deleted space: the space is restored whole.</param>
public sealed record BinPageDto(Guid Id, Guid SpaceId, string Kind, string Title, string ContentJson, IReadOnlyList<string> Path,
    DateTimeOffset UpdatedAt, string UpdatedBy, DateTimeOffset DeletedAt, string DeletedBy, Guid? RestoreId, IReadOnlyList<BinNodeDto> Children);
public sealed record BinItemDto(Guid Id, Guid SpaceId, string SpaceName, string Kind, string Title, string Location, int Items, DateTimeOffset DeletedAt, string DeletedBy, bool Hidden);
public sealed record RestoredPageDto(Guid Id, Guid SpaceId);
