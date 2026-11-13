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

public sealed record BinItemDto(Guid Id, Guid SpaceId, string SpaceName, string Kind, string Title, string Location, int Items, DateTimeOffset DeletedAt, string DeletedBy, bool Hidden);
public sealed record RestoredPageDto(Guid Id, Guid SpaceId);
